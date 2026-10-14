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
        IncludeThroughScanner();
        CreateFoldersOnlyFirstRun();
        NoHashMode();
        LegacyJobMigration();
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

    // --- Include phai di qua Scanner: thu muc con khong bi chan boi mau chi danh cho file ---
    private static void IncludeThroughScanner()
    {
        var root = NewTempDir("inc-scan");
        Write(root, "a.txt", "1"); Write(root, "sub/b.txt", "2"); Write(root, "sub/deep/c.txt", "3");
        Write(root, "sub/x.log", "4"); Write(root, "logs/y.txt", "5"); Write(root, "cache/z.txt", "6");

        var inc = new FolderScanner(new JobOptions { IncludePatterns = "*.txt" }.BuildFilter()).Scan(root).Entries;
        Check("Include *.txt qua Scanner: lay ca file trong thu muc con",
            inc.Count == 5 && inc.ContainsKey("sub/deep/c.txt") && !inc.ContainsKey("sub/x.log"),
            string.Join(",", inc.Keys));

        var exc = new FolderScanner(new JobOptions { ExcludePatterns = "logs/*\ncache" }.BuildFilter()).Scan(root).Entries;
        Check("Exclude 'logs/*' va 'cache' qua Scanner: bo ca thu muc",
            exc.Count == 4 && !exc.ContainsKey("logs/y.txt") && !exc.ContainsKey("cache/z.txt"),
            string.Join(",", exc.Keys));
    }

    // --- CreateFoldersIfMissing: chi lan chay dau, khong bao gio qua mat chot RootMissing ---
    private static void CreateFoldersOnlyFirstRun()
    {
        var root = NewTempDir("mk");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R-chua-co");
        Directory.CreateDirectory(left);
        for (int i = 0; i < 10; i++) Write(left, $"f{i}.txt", "d" + i);
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        SyncSession Session() => new("j", left, right, SyncMode.TwoWay, store,
            new SyncOptions(MaxDeletePercent: 100), createMissingRoots: true);

        var preview = Session().AnalyzeOnly();
        Check("CreateFolders: Analyze KHONG tao folder", !Directory.Exists(right));

        var first = Session().Run();
        Check("CreateFolders: lan chay dau tao folder dich va copy du",
            Directory.Exists(right) && first.Result!.Copied == 10
            && first.ScanErrors.Any(e => e.Contains("Da tao folder")), $"copied={first.Result?.Copied}");

        // Trai bien mat sau khi da co state (USB bi rut) - khong duoc tu tao lai roi xoa sach Phai
        Directory.Move(left, left + "-mat");
        var second = Session().Run();
        Check("CreateFolders: da co state, goc mat -> KHONG tao lai, huy, Phai con du 10 file (ke ca nguong 100%)",
            !Directory.Exists(left) && second.Result!.Aborted && Directory.GetFiles(right).Length == 10,
            $"aborted={second.Result?.Aborted} left ton tai={Directory.Exists(left)} con={Directory.GetFiles(right).Length}");
    }

    // --- Tat CompareChecksums: file giong het khong thanh xung dot, state so ca mtime ---
    private static void NoHashMode()
    {
        var root = NewTempDir("nohash");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        Write(left, "same.txt", "SAME"); Write(right, "same.txt", "SAME");
        File.SetLastWriteTimeUtc(Full(right, "same.txt"), File.GetLastWriteTimeUtc(Full(left, "same.txt")));
        Write(left, "diff.txt", "AAAA"); Write(right, "diff.txt", "BBBB");
        File.SetLastWriteTimeUtc(Full(right, "diff.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        var session = new SyncSession("j", left, right, SyncMode.TwoWay, store, computeHashes: false);

        var acts = session.AnalyzeOnly().Actions.ToDictionary(a => a.RelativePath, a => a.Kind);
        Check("Khong hash: 2 file giong het (cung size+mtime) -> Skip, khong xung dot gia",
            acts["same.txt"] == SyncActionKind.Skip, acts["same.txt"].ToString());
        Check("Khong hash: cung size khac mtime -> van la xung dot", acts["diff.txt"] == SyncActionKind.Conflict,
            acts["diff.txt"].ToString());

        session.Run();
        var state = store.LoadState("j");
        Check("Khong hash: state chi ghi file giong ca size+mtime (diff.txt khong vao state)",
            state.ContainsKey("same.txt") && !state.ContainsKey("diff.txt"), string.Join(",", state.Keys));
    }

    // --- DB cu (chua co settings_json): Job cu giu hanh vi cu, khong tu xoa history ---
    private static void LegacyJobMigration()
    {
        var dbPath = Path.Combine(NewTempDir("legacy"), "s.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE jobs (id TEXT PRIMARY KEY, name TEXT NOT NULL, left_root TEXT NOT NULL,
                    right_root TEXT NOT NULL, mode INTEGER NOT NULL, conflict_policy INTEGER NOT NULL DEFAULT 0);
                INSERT INTO jobs VALUES ('old', 'Job cu', 'C:/a', 'C:/b', 2, 0);
                """;
            cmd.ExecuteNonQuery();
        }
        using var store = new SqliteStateStore(dbPath);
        var o = JobOptions.FromJson(store.ListJobs().Single().SettingsJson);
        Check("Migration: Job cu KHONG bat tu don history / copy thuoc tinh",
            !o.CleanupHistory && !o.CopyAttributes && !o.CopyCreationTime && o.CompareChecksums);
        Check("FromJson(\"{}\") = Legacy, FromJson(null) = mac dinh Job moi",
            !JobOptions.FromJson("{}").CleanupHistory && JobOptions.FromJson(null).CleanupHistory);
    }
}
