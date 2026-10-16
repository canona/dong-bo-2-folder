using System.Security.Cryptography;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using Microsoft.Data.Sqlite;

// Oracle cho bo test kho (HARD-*): kiem bat bien DOC LAP voi engine. Khong goi FileHasher/FolderScanner/
// SqliteStateStore - tu duyet thu muc, tu tinh SHA-256 va doc thang file SQLite, de loi cua engine
// khong the "tu bao cao dung". Moi ham tra danh sach vi pham (rong = dat).
public static class HardOracle
{
    public const string HistoryDir = ".foldersync_history";

    public sealed record StateRow(string Path, long Size, string? Hash);

    // ================= Tien ich doc lap =================

    public static string Sha(string fullPath)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static bool InHistory(string rel)
        => rel.Split('/').Any(s => s.Equals(HistoryDir, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> AllFiles(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            : Enumerable.Empty<string>();

    /// <summary>Anh chup 1 phia: duong dan tuong doi -> SHA-256. Bo history va file tam.</summary>
    public static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in AllFiles(root))
        {
            if (InHistory(rel) || rel.EndsWith(".tmp-sync", StringComparison.OrdinalIgnoreCase)) continue;
            map[rel] = Sha(Full(root, rel));
        }
        return map;
    }

    /// <summary>Doc thang bang file_state trong SQLite (khong qua SqliteStateStore).</summary>
    public static List<StateRow> ReadStateRows(string dbPath, string jobId)
    {
        var rows = new List<StateRow>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rel_path, size, hash FROM file_state WHERE job_id = $j ORDER BY rel_path";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new StateRow(rd.GetString(0), rd.GetInt64(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        return rows;
    }

    // ================= (a) Lan phan tich thu 2 phai rong =================

    /// <summary>Sau khi sync xong, AnalyzeOnly() tren session MOI khong duoc con hanh dong nao (ngoai Skip).</summary>
    public static List<string> AssertAnalyzeLan2Rong(SyncSession fresh)
    {
        var v = new List<string>();
        var actions = fresh.AnalyzeOnly().Actions;
        foreach (var a in actions.Where(a => a.Kind != SyncActionKind.Skip))
            v.Add($"(a) lan analyze 2 con hanh dong {a.Kind} {a.RelativePath}{(a.TargetPath is null ? "" : " -> " + a.TargetPath)}");
        return v;
    }

    // ================= (b) State khop dia =================

    /// <summary>Moi dong file_state: file co o CA 2 phia, size khop dong state, SHA-256 2 phia bang nhau
    /// (va bang hash trong state neu co). Tu quet dia, khong tin ket qua engine.</summary>
    /// <param name="staleOk">Duong dan nguoi dung vua sua/xoa GIUA luc chay: state duoc phep la moc cu (ban da kiem
    /// chung luc chep / ban truoc khi xoa) - nhung PHAI khop that voi it nhat 1 phia dang con, khong thi la state noi doi.</param>
    public static List<string> AssertStateKhopDia(string dbPath, string jobId, string left, string right,
        ISet<string>? staleOk = null)
    {
        var v = new List<string>();
        foreach (var row in ReadStateRows(dbPath, jobId))
        {
            var l = Full(left, row.Path); var r = Full(right, row.Path);
            if (staleOk is not null && staleOk.Contains(row.Path) && row.Hash is not null
                && new[] { l, r }.Any(f => File.Exists(f) && string.Equals(Sha(f), row.Hash, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!File.Exists(l) || !File.Exists(r))
            {
                v.Add($"(b) state ghi '{row.Path}' nhung thieu o {(File.Exists(l) ? "Phai" : "Trai")}");
                continue;
            }
            if (new FileInfo(l).Length != row.Size || new FileInfo(r).Length != row.Size)
                v.Add($"(b) '{row.Path}': size state={row.Size} trai={new FileInfo(l).Length} phai={new FileInfo(r).Length}");
            string hl = Sha(l), hr = Sha(r);
            if (hl != hr) v.Add($"(b) '{row.Path}' state noi da sync nhung noi dung 2 phia KHAC nhau");
            else if (row.Hash is not null && !string.Equals(row.Hash, hl, StringComparison.OrdinalIgnoreCase))
                v.Add($"(b) '{row.Path}': hash trong state khac hash that cua file");
        }
        return v;
    }

    // ================= (c) Khong con file tam =================

    public static List<string> AssertKhongConFileTam(params string[] roots)
    {
        var v = new List<string>();
        foreach (var root in roots)
            foreach (var rel in AllFiles(root).Where(f => f.EndsWith(".tmp-sync", StringComparison.OrdinalIgnoreCase)))
                v.Add($"(c) con file tam {Path.Combine(root, rel)}");
        return v;
    }

    // ================= (d) History bao toan =================

    /// <summary>Moi file cua 1 phia bi xoa/ghi de giua anh chup truoc (pre) va sau (post) deu phai co ban trong
    /// .foldersync_history/&lt;stamp&gt;/ voi SHA-256 dung bang ban goc. File chi doi ten (noi dung ton tai o duong
    /// dan moi) va file nguoi dung tu sua (userTouched) khong tinh.</summary>
    public static List<string> AssertHistoryBaoToan(string root, IReadOnlyDictionary<string, string> pre,
        IReadOnlyDictionary<string, string> post, IEnumerable<string>? userTouched = null)
    {
        var v = new List<string>();
        var touched = new HashSet<string>(userTouched ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var histRoot = Path.Combine(root, HistoryDir);
        if (Directory.Exists(histRoot))
            foreach (var stamp in Directory.GetDirectories(histRoot))
                foreach (var rel in AllFiles(stamp))
                {
                    if (!history.TryGetValue(rel, out var set)) history[rel] = set = new HashSet<string>();
                    set.Add(Sha(Full(stamp, rel)));
                }
        var movedTargets = post.Where(p => !pre.ContainsKey(p.Key)).Select(p => p.Value).ToHashSet();
        foreach (var (rel, sha) in pre)
        {
            if (touched.Contains(rel)) continue;
            bool gone = !post.TryGetValue(rel, out var now);
            if (!gone && now == sha) continue;
            if (gone && movedTargets.Contains(sha)) continue; // doi ten: noi dung van con o duong dan moi
            if (!history.TryGetValue(rel, out var set) || !set.Contains(sha))
                v.Add($"(d) '{rel}' bi {(gone ? "xoa" : "ghi de")} nhung history khong co ban dung hash goc ({root})");
        }
        return v;
    }

    // ================= (e) Khong vuot chot xoa =================

    /// <summary>So file bi xoa THAT (o ca 2 phia, tru doi ten va file nguoi dung tu xoa) khong vuot nguong
    /// % va tuyet doi da cau hinh. baseline = so file trong state truoc khi chay, hoac tong file 2 phia neu chua co state.</summary>
    public static List<string> AssertKhongVuotChotXoa(int baseline, SyncOptions options,
        IReadOnlyDictionary<string, string> preLeft, IReadOnlyDictionary<string, string> postLeft,
        IReadOnlyDictionary<string, string> preRight, IReadOnlyDictionary<string, string> postRight,
        IEnumerable<string>? userTouched = null)
    {
        var v = new List<string>();
        if (options.Force) return v;
        var touched = new HashSet<string>(userTouched ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        int deleted = CountDeleted(preLeft, postLeft, touched) + CountDeleted(preRight, postRight, touched);
        if (deleted > options.MaxDeleteAbsolute)
            v.Add($"(e) {deleted} file bi xoa > toi da {options.MaxDeleteAbsolute}");
        if (baseline > 0 && deleted * 100.0 / baseline > options.MaxDeletePercent)
            v.Add($"(e) {deleted}/{baseline} file bi xoa > {options.MaxDeletePercent}%");
        return v;
    }

    private static int CountDeleted(IReadOnlyDictionary<string, string> pre, IReadOnlyDictionary<string, string> post,
        HashSet<string> touched)
    {
        var movedTargets = post.Where(p => !pre.ContainsKey(p.Key)).Select(p => p.Value).ToHashSet();
        return pre.Count(p => !touched.Contains(p.Key) && !post.ContainsKey(p.Key) && !movedTargets.Contains(p.Value));
    }

    // ================= (f) Khong sync thu muc history =================

    /// <summary>Khong hanh dong nao dung toi .foldersync_history, va khong co history long trong history.</summary>
    public static List<string> AssertKhongSyncThuMucHistory(IEnumerable<SyncAction> actions, params string[] roots)
    {
        var v = new List<string>();
        foreach (var a in actions)
            if (InHistory(a.RelativePath) || (a.TargetPath is { } t && InHistory(t)))
                v.Add($"(f) hanh dong {a.Kind} dung toi history: {a.RelativePath}{(a.TargetPath is null ? "" : " -> " + a.TargetPath)}");
        foreach (var root in roots)
            foreach (var rel in AllFiles(root))
            {
                var parts = rel.Split('/');
                if (parts.Count(s => s.Equals(HistoryDir, StringComparison.OrdinalIgnoreCase)) > 1)
                    v.Add($"(f) history long nhau: {Path.Combine(root, rel)}");
            }
        return v;
    }
}
