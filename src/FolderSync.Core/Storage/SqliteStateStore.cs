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
        }
    }

    public IReadOnlyList<JobRecord> ListJobs()
    {
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

    /// <summary>Xoa Job cung toan bo state va nhat ky chay cua no. Khong dong vao file du lieu that.</summary>
    public void DeleteJob(string id)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var table in new[] { "jobs", "file_state", "sync_runs" })
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

    public void UpsertJob(string id, string name, string leftRoot, string rightRoot, SyncMode mode,
        ConflictPolicy policy, string settingsJson = "{}")
    {
        using var cmd = _conn.CreateCommand();
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
    }

    public IReadOnlyDictionary<string, FileEntry> LoadState(string jobId)
    {
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

    public void SaveState(string jobId, IReadOnlyDictionary<string, FileEntry> entries)
    {
        using var tx = _conn.BeginTransaction();
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM file_state WHERE job_id = $job";
            del.Parameters.AddWithValue("$job", jobId);
            del.ExecuteNonQuery();
        }
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

    public void SaveRun(string jobId, DateTime startedUtc, DateTime finishedUtc,
        int copied, int deleted, int moved, int conflicts, int skipped, int errors)
    {
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

    public int CountRuns(string jobId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_runs WHERE job_id = $job";
        cmd.Parameters.AddWithValue("$job", jobId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose() => _conn.Dispose();
}
