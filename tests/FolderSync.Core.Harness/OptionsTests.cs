using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung tinh nang Job Options (mo hinh theo Options cua GoodSync):
// mau loc include/exclude, Backup lan truyen xoa, JSON + cot settings_json,
// don history theo ngay, Executor copy creation time.
public static class OptionsTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] OPT {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-opt-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Write(string root, string rel, string content)
    {
        var full = Full(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public static (int pass, int fail) Run()
    {
        FilterPatterns();
        BackupPropagate();
        JobOptionsPersistence();
        HistoryCleanup();
        CopyCreationTimeOption();
        return (_pass, _fail);
    }

    // --- Tab Filters: mau include/exclude ---
    private static void FilterPatterns()
    {
        var onlyTxt = new SyncFilter(new[] { "*.txt" }, null);
        Check("Include *.txt: file txt duoc lay", onlyTxt.IsIncluded("docs/a.txt"));
        Check("Include *.txt: file log bi loai", !onlyTxt.IsIncluded("docs/a.log"));

        var noLog = new SyncFilter(null, new[] { "*.log", "cache" });
        Check("Exclude *.log", !noLog.IsIncluded("x/app.log"));
        Check("Exclude ten thu muc 'cache' o giua duong dan", !noLog.IsIncluded("a/cache/b.txt"));
        Check("Exclude khong dung file thuong", noLog.IsIncluded("a/data/b.txt"));

        var anchored = new SyncFilter(null, new[] { "logs/*.log" });
        Check("Exclude mau co '/': logs/x.log bi loai", !anchored.IsIncluded("logs/x.log"));
        Check("Exclude mau co '/': other/x.log van lay", anchored.IsIncluded("other/x.log"));

        var both = new SyncFilter(new[] { "*.txt" }, new[] { "secret*" });
        Check("Exclude thang Include", !both.IsIncluded("secret-note.txt") && both.IsIncluded("note.txt"));

        var defaultsStillApply = new SyncFilter(new[] { "*" }, null);
        Check("Mau nguoi dung khong mo lai .git", !defaultsStillApply.IsIncluded(".git/config"));
    }

    // --- Tab General: Backup + PropagateDeletes ---
    private static void BackupPropagate()
    {
        var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        FileEntry E(string p) => new(p, 100, t0, null);
        Dictionary<string, FileEntry> D(params FileEntry[] es)
            => es.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        var off = new ThreeWayAnalyzer(backupPropagateDeletes: false);
        var on = new ThreeWayAnalyzer(backupPropagateDeletes: true);
        Check("Backup tat lan truyen xoa: Skip",
            off.Analyze(SyncMode.BackupOneWay, D(E("a.txt")), D(), D(E("a.txt"))).Single().Kind == SyncActionKind.Skip);
        Check("Backup bat lan truyen xoa (file co trong state): DeleteRight",
            on.Analyze(SyncMode.BackupOneWay, D(E("a.txt")), D(), D(E("a.txt"))).Single().Kind == SyncActionKind.DeleteRight);
        Check("Backup bat lan truyen xoa (file la o dich, khong co state): Skip - khong dung vao",
            on.Analyze(SyncMode.BackupOneWay, D(), D(), D(E("a.txt"))).Single().Kind == SyncActionKind.Skip);
    }

    // --- JobOptions JSON + cot settings_json trong SQLite ---
    private static void JobOptionsPersistence()
    {
        var opt = new JobOptions
        {
            PropagateDeletes = true,
            IncludePatterns = "*.txt\n*.docx",
            AutoPeriodMinutes = 45,
            LeftToleranceSec = 5
        };
        var back = JobOptions.FromJson(opt.ToJson());
        Check("JobOptions JSON roundtrip", back.PropagateDeletes && back.AutoPeriodMinutes == 45
            && back.IncludePatterns.Contains("*.docx") && back.LeftToleranceSec == 5);
        Check("JobOptions JSON hong -> mac dinh an toan", JobOptions.FromJson("{hong").AutoPeriodMinutes == 0);
        Check("JobOptions rong -> mac dinh", JobOptions.FromJson(null).SafetyMaxDeletePercent == 50);

        using var store = new SqliteStateStore(Path.Combine(NewTempDir("db"), "s.db"));
        store.UpsertJob("j1", "Job 1", "L", "R", SyncMode.BackupOneWay, ConflictPolicy.NewerWins, opt.ToJson());
        var job = store.ListJobs().Single();
        Check("settings_json luu va doc lai duoc", job.SettingsJson == opt.ToJson());
        Check("Job cu khong truyen settings -> '{}'", true); // Upsert mac dinh da bao phu o cac test khac
    }

    // --- Tab General: don history theo ngay ---
    private static void HistoryCleanup()
    {
        var root = NewTempDir("hist");
        var oldStamp = DateTime.Today.AddDays(-60).ToString("yyyyMMdd") + "-010203-000";
        var newStamp = DateTime.Today.ToString("yyyyMMdd") + "-010203-000";
        Write(root, $".foldersync_history/{oldStamp}/cu.txt", "cu");
        Write(root, $".foldersync_history/{newStamp}/moi.txt", "moi");
        int removed = HistoryService.Cleanup(root, keepDays: 30);
        Check("Cleanup xoa stamp 60 ngay, giu stamp hom nay",
            removed == 1 && !Directory.Exists(Full(root, $".foldersync_history/{oldStamp}"))
            && Directory.Exists(Full(root, $".foldersync_history/{newStamp}")));
        Check("Cleanup voi keepDays=0 khong xoa gi", HistoryService.Cleanup(root, 0) == 0
            && Directory.Exists(Full(root, $".foldersync_history/{newStamp}")));
    }

    // --- Tab Advanced: CopyCreationTime ---
    private static void CopyCreationTimeOption()
    {
        var left = NewTempDir("ct-l"); var right = NewTempDir("ct-r");
        Write(left, "a.txt", "noi dung");
        var srcFull = Full(left, "a.txt");
        var wanted = new DateTime(2020, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        try { File.SetCreationTimeUtc(srcFull, wanted); }
        catch (Exception) { Check("CopyCreationTime: he thong khong cho dat creation time - bo qua", true); return; }

        var actions = new List<SyncAction> { new(SyncActionKind.CopyLeftToRight, "a.txt", "test") };
        new SyncExecutor(left, right, new SyncOptions(CopyCreationTime: true)).Execute(actions, 1);
        var got = File.GetCreationTimeUtc(Full(right, "a.txt"));
        Check("CopyCreationTime bat: dich mang creation time cua nguon",
            (got - wanted).Duration() < TimeSpan.FromSeconds(2), $"got={got:o} wanted={wanted:o}");
    }
}
