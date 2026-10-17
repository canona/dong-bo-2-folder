using FolderSync.Core.Models;
using Microsoft.Data.Sqlite;

namespace FolderSync.Core.Storage;

public sealed record JobRecord(string Id, string Name, string LeftRoot, string RightRoot,
    SyncMode Mode, ConflictPolicy Policy, string SettingsJson = "{}");

/// <summary>
/// Kho trang thai dong bo trong 1 file SQLite duy nhat (Ke hoach muc 3, 10):
/// - jobs: dinh nghia Job (2 folder, che do, chinh sach xung dot).
/// - file_state: snapshot sau lan sync thanh cong gan nhat - "State cu" cho so sanh 3 chieu.
/// - sync_runs: nhat ky tung lan chay (phuc vu truy vet va bao cao).
/// SaveState thay the toan bo snapshot cua Job trong 1 transaction.
/// </summary>
public sealed class SqliteStateStore : IDisposable
{
    private readonly SqliteConnection _conn;
    // 1 connection dung chung cho moi Job; Microsoft.Data.Sqlite KHONG an toan da luong ->
    // cac Job chay song song (JobRunner) phai tuan tu hoa moi truy cap qua khoa nay.
    private readonly object _sync = new();

    /// <summary>Diem chot chich loi DuringSaveState (xem FaultPoints) - mac dinh null, chi harness gan.</summary>
    internal Action<string, string?>? FaultHook { get; set; }

    public SqliteStateStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                left_root TEXT NOT NULL,
                right_root TEXT NOT NULL,
                mode INTEGER NOT NULL,
                conflict_policy INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS file_state (
                job_id TEXT NOT NULL,
                rel_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                mtime_utc_ticks INTEGER NOT NULL,
                hash TEXT NULL,
                PRIMARY KEY (job_id, rel_path)
            );
            CREATE TABLE IF NOT EXISTS sync_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                finished_utc_ticks INTEGER NOT NULL,
                copied INTEGER NOT NULL, deleted INTEGER NOT NULL, moved INTEGER NOT NULL,
                conflicts INTEGER NOT NULL, skipped INTEGER NOT NULL, errors INTEGER NOT NULL
            );
            -- Cache hash theo Job + phia (0 = Trai, 1 = Phai): file cung size + mtime thi khong doc lai noi dung
            -- (ke ca sau khi tat/mo app). Xoa khi doi folder / xoa Job.
            CREATE TABLE IF NOT EXISTS hash_cache (
                job_id TEXT NOT NULL,
                side INTEGER NOT NULL,
                rel_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                mtime_utc_ticks INTEGER NOT NULL,
                hash TEXT NOT NULL,
                PRIMARY KEY (job_id, side, rel_path)
            );
            """;
        cmd.ExecuteNonQuery();

        // Migration: DB cua ban cu chua co cot settings_json (Job Options dang JSON)
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = 'settings_json'";
        if (Convert.ToInt64(check.ExecuteScalar()) == 0)
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE jobs ADD COLUMN settings_json TEXT NOT NULL DEFAULT '{}'";
            alter.ExecuteNonQuery();
            // Job da co truoc migration giu nguyen hanh vi cu (xem JobOptions.Legacy)
            using var legacy = _conn.CreateCommand();
            legacy.CommandText = "UPDATE jobs SET settings_json = $json";
            legacy.Parameters.AddWithValue("$json", JobOptions.Legacy().ToJson());
            legacy.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<JobRecord> ListJobs()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, left_root, right_root, mode, conflict_policy, settings_json FROM jobs ORDER BY name";
            var result = new List<JobRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JobRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    (SyncMode)reader.GetInt32(4), (ConflictPolicy)reader.GetInt32(5),
                    reader.IsDBNull(6) ? "{}" : reader.GetString(6)));
            }
            return result;
        }
    }

    /// <summary>Xoa Job cung toan bo state va nhat ky chay cua no. Khong dong vao file du lieu that.</summary>
    public void DeleteJob(string id)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _conn.BeginTransaction();
            foreach (var table in new[] { "jobs", "file_state", "sync_runs", "hash_cache" })
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = table == "jobs"
                    ? "DELETE FROM jobs WHERE id = $id"
                    : $"DELETE FROM {table} WHERE job_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Them/sua Job. Neu Job da ton tai va folder Trai hoac Phai THAY DOI thi xoa state cu trong cung
    /// transaction: state la anh chup cua CAP FOLDER CU - giu lai thi file "co trong state ma khong co
    /// o folder moi" bi hieu la nguoi dung da xoa -> lan truyen xoa sang phia kia.
    /// </summary>
    /// <returns>true neu state cu da bi xoa vi doi folder.</returns>
    public bool UpsertJob(string id, string name, string leftRoot, string rightRoot, SyncMode mode,
        ConflictPolicy policy, string settingsJson = "{}")
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _conn.BeginTransaction();
            bool rootsChanged = false;
            using (var get = _conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = "SELECT left_root, right_root FROM jobs WHERE id = $id";
                get.Parameters.AddWithValue("$id", id);
                using var reader = get.ExecuteReader();
                if (reader.Read())
                    rootsChanged = !SamePath(reader.GetString(0), leftRoot) || !SamePath(reader.GetString(1), rightRoot);
            }

            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO jobs (id, name, left_root, right_root, mode, conflict_policy, settings_json)
                VALUES ($id, $name, $l, $r, $mode, $policy, $settings)
                ON CONFLICT(id) DO UPDATE SET name=$name, left_root=$l, right_root=$r,
                    mode=$mode, conflict_policy=$policy, settings_json=$settings;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$l", leftRoot);
            cmd.Parameters.AddWithValue("$r", rightRoot);
            cmd.Parameters.AddWithValue("$mode", (int)mode);
            cmd.Parameters.AddWithValue("$policy", (int)policy);
            cmd.Parameters.AddWithValue("$settings", settingsJson);
            cmd.ExecuteNonQuery();

            if (rootsChanged)
            {
                using var clear = _conn.CreateCommand();
                clear.Transaction = tx;
                // Cache hash cung theo duong dan tuong doi cua folder CU -> xoa luon
                clear.CommandText = "DELETE FROM file_state WHERE job_id = $id; DELETE FROM hash_cache WHERE job_id = $id;";
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }
            tx.Commit();
            return rootsChanged;
        }
    }

    /// <summary>So 2 duong dan folder theo kieu Windows: khong phan biet hoa/thuong, bo dau phan cach o cuoi.</summary>
    private static bool SamePath(string a, string b)
    {
        static string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception) { /* duong dan khong hop le - so nguyen van */ }
            return p.TrimEnd('\\', '/');
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, FileEntry> LoadState(string jobId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT rel_path, size, mtime_utc_ticks, hash FROM file_state WHERE job_id = $job";
            cmd.Parameters.AddWithValue("$job", jobId);
            var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                result[path] = new FileEntry(
                    path,
                    reader.GetInt64(1),
                    new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }
            return result;
        }
    }

    public void SaveState(string jobId, IReadOnlyDictionary<string, FileEntry> entries)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM file_state WHERE job_id = $job";
                del.Parameters.AddWithValue("$job", jobId);
                del.ExecuteNonQuery();
            }
            FaultHook?.Invoke(FolderSync.Core.Engine.FaultPoints.DuringSaveState, null);
            using var ins = _conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO file_state (job_id, rel_path, size, mtime_utc_ticks, hash)
                VALUES ($job, $path, $size, $mtime, $hash);
                """;
            var pJob = ins.Parameters.Add("$job", SqliteType.Text);
            var pPath = ins.Parameters.Add("$path", SqliteType.Text);
            var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
            var pMtime = ins.Parameters.Add("$mtime", SqliteType.Integer);
            var pHash = ins.Parameters.Add("$hash", SqliteType.Text);
            pJob.Value = jobId;
            foreach (var e in entries.Values)
            {
                pPath.Value = e.RelativePath;
                pSize.Value = e.Size;
                pMtime.Value = e.MtimeUtc.Ticks;
                pHash.Value = (object?)e.Hash ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void SaveRun(string jobId, DateTime startedUtc, DateTime finishedUtc,
        int copied, int deleted, int moved, int conflicts, int skipped, int errors)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_runs (job_id, started_utc_ticks, finished_utc_ticks,
                    copied, deleted, moved, conflicts, skipped, errors)
                VALUES ($job, $s, $f, $c, $d, $m, $cf, $sk, $e);
                """;
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$s", startedUtc.Ticks);
            cmd.Parameters.AddWithValue("$f", finishedUtc.Ticks);
            cmd.Parameters.AddWithValue("$c", copied);
            cmd.Parameters.AddWithValue("$d", deleted);
            cmd.Parameters.AddWithValue("$m", moved);
            cmd.Parameters.AddWithValue("$cf", conflicts);
            cmd.Parameters.AddWithValue("$sk", skipped);
            cmd.Parameters.AddWithValue("$e", errors);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Doc cache hash cua Job: (Trai, Phai) - moi phia la dictionary theo duong dan tuong doi.</summary>
    public (Dictionary<string, FileEntry> Left, Dictionary<string, FileEntry> Right) LoadHashCache(string jobId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var left = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            var right = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT side, rel_path, size, mtime_utc_ticks, hash FROM hash_cache WHERE job_id = $job";
            cmd.Parameters.AddWithValue("$job", jobId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                var entry = new FileEntry(path, reader.GetInt64(2), new DateTime(reader.GetInt64(3), DateTimeKind.Utc), reader.GetString(4));
                (reader.GetInt32(0) == 0 ? left : right)[path] = entry;
            }
            return (left, right);
        }
    }

    /// <summary>Ghi THAY DOI cua cache hash (them/sua + xoa) trong 1 transaction - khong ghi lai toan bo.</summary>
    public void SaveHashCacheDelta(string jobId, bool left, IReadOnlyCollection<FileEntry> upserts, IReadOnlyCollection<string> deletes)
    {
        if (upserts.Count == 0 && deletes.Count == 0) return;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var tx = _conn.BeginTransaction();
            if (deletes.Count > 0)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM hash_cache WHERE job_id = $job AND side = $side AND rel_path = $path";
                del.Parameters.AddWithValue("$job", jobId);
                del.Parameters.AddWithValue("$side", left ? 0 : 1);
                var pPath = del.Parameters.Add("$path", SqliteType.Text);
                foreach (var path in deletes) { pPath.Value = path; del.ExecuteNonQuery(); }
            }
            if (upserts.Count > 0)
            {
                using var ins = _conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO hash_cache (job_id, side, rel_path, size, mtime_utc_ticks, hash)
                    VALUES ($job, $side, $path, $size, $mtime, $hash)
                    ON CONFLICT(job_id, side, rel_path) DO UPDATE SET size=$size, mtime_utc_ticks=$mtime, hash=$hash;
                    """;
                ins.Parameters.AddWithValue("$job", jobId);
                ins.Parameters.AddWithValue("$side", left ? 0 : 1);
                var pPath = ins.Parameters.Add("$path", SqliteType.Text);
                var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
                var pMtime = ins.Parameters.Add("$mtime", SqliteType.Integer);
                var pHash = ins.Parameters.Add("$hash", SqliteType.Text);
                foreach (var e in upserts)
                {
                    pPath.Value = e.RelativePath; pSize.Value = e.Size; pMtime.Value = e.MtimeUtc.Ticks; pHash.Value = e.Hash!;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public int CountRuns(string jobId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sync_runs WHERE job_id = $job";
            cmd.Parameters.AddWithValue("$job", jobId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private bool _disposed;

    /// <summary>
    /// Lay CUNG khoa voi moi thao tac: doi thao tac dang do (vd JobRunner chay nen dang ghi state) xong roi moi dong.
    /// Dong ngang giua 1 transaction tung lam sap ca tien trinh (NullReferenceException trong SqliteTransaction).
    /// Sau khi dong, moi lenh goi -> ObjectDisposedException (JobRunner bat va bao loi binh thuong).
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }
}
