using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;
using Microsoft.Data.Sqlite;

// Ban sua loi P0 H1 (b: file_state mat, c: settings_json hong): cac luong xung quanh chot moi - xac nhan chay lai,
// state rong HOP LE khong bi bao nham, migration DB cu, doi folder, cau hinh hong. Repro goc nam o TimeCorruptionHardTests.
public static class StateLostTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] H1FIX {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-h1fix-{label}-{Guid.NewGuid():N}");
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

    private static int CountFiles(string root)
        => Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.Contains(".foldersync_history"))
            : 0;

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- H1FIX: state bi mat / cau hinh hong ---");
        AcceptLostStateFlow();
        LegitEmptyStateNotFlagged();
        CopyOnlyRunHealsLostState();
        RootChangeClearsMarker();
        MigrationInitializesMarker();
        CorruptDbReleasesFile();
        CorruptSettings();
        return (_pass, _fail);
    }

    /// <summary>Mirror, state mat, Trai mat 12/20 -> bi chan (StateLost) -> nguoi dung xac nhan -> chay nhu lan dau.</summary>
    private static void AcceptLostStateFlow()
    {
        var root = NewTempDir("accept");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        for (int i = 0; i < 20; i++) Write(left, $"f{i:00}.txt", "v" + i);
        var db = Path.Combine(root, "s.db");
        using var store = new SqliteStateStore(db);
        store.UpsertJob("acc", "acc", left, right, SyncMode.MirrorOneWay, ConflictPolicy.NewerWins);
        SyncSession S() => new("acc", left, right, SyncMode.MirrorOneWay, store, new SyncOptions(MaxDeletePercent: 50), ConflictPolicy.NewerWins);
        S().Run();
        Check("Sau lan dong bo dau, jobs.state_files = 20", store.LoadStateFileCount("acc") == 20, $"{store.LoadStateFileCount("acc")}");

        // Mo phong MAT state bang SQL truc tiep (SaveState rong la ghi HOP LE, dat state_files = 0)
        Exec(db, "DELETE FROM file_state;");
        for (int i = 0; i < 6; i++) File.Delete(Full(left, $"f{i:00}.txt")); // 6/20 = 30%: chot % thuong KHONG chan
        var analyze = S().AnalyzeOnly();
        Check("Phan tich: bao StateLost, khong dong vao file", analyze.StateLost && CountFiles(right) == 20);
        var blocked = S().Run();
        Check("Dong bo khi state mat + co lenh xoa: HUY, Phai con du 20 file",
            blocked.StateLost && blocked.Result?.Aborted == true && CountFiles(right) == 20,
            $"aborted={blocked.Result?.Aborted} phai={CountFiles(right)} ly do={blocked.Result?.AbortReason}");
        Check("Force cung khong vuot duoc chot state mat",
            new SyncSession("acc", left, right, SyncMode.MirrorOneWay, store, new SyncOptions(Force: true), ConflictPolicy.NewerWins)
                .Run().Result?.Aborted == true && CountFiles(right) == 20);

        store.AcceptLostState("acc");
        var after = S().Run();
        Check("Sau khi nguoi dung xac nhan: chay nhu lan dau, Mirror xoa 6 file (vao history), het StateLost",
            !after.StateLost && after.Result?.Aborted == false && CountFiles(right) == 14
            && Directory.Exists(Full(right, ".foldersync_history")),
            $"aborted={after.Result?.Aborted} phai={CountFiles(right)}");
        Check("state_files cap nhat lai sau lan chay", store.LoadStateFileCount("acc") == 14, $"{store.LoadStateFileCount("acc")}");
    }

    /// <summary>2 folder rong dong bo xong -> state rong HOP LE (state_files = 0) -> khong bao mat state.</summary>
    private static void LegitEmptyStateNotFlagged()
    {
        var root = NewTempDir("empty");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        store.UpsertJob("emp", "emp", left, right, SyncMode.MirrorOneWay, ConflictPolicy.NewerWins);
        new SyncSession("emp", left, right, SyncMode.MirrorOneWay, store).Run();
        Write(right, "thua.txt", "x");
        for (int i = 0; i < 3; i++) Write(left, $"a{i}.txt", "a");
        var o = new SyncSession("emp", left, right, SyncMode.MirrorOneWay, store).Run();
        Check("State rong hop le (2 folder rong) -> khong bao StateLost, Mirror chay binh thuong",
            !o.StateLost && o.Result?.Aborted == false && !File.Exists(Full(right, "thua.txt")),
            $"stateLost={o.StateLost} aborted={o.Result?.Aborted} ly do={o.Result?.AbortReason}");
    }

    /// <summary>State mat nhung lan chay chi co lenh chep (Backup) -> chay duoc, state duoc dung lai, het canh bao.</summary>
    private static void CopyOnlyRunHealsLostState()
    {
        var root = NewTempDir("heal");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        for (int i = 0; i < 5; i++) Write(left, $"f{i}.txt", "v" + i);
        var db = Path.Combine(root, "s.db");
        using var store = new SqliteStateStore(db);
        store.UpsertJob("heal", "heal", left, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins);
        new SyncSession("heal", left, right, SyncMode.BackupOneWay, store).Run();
        Exec(db, "DELETE FROM file_state;");
        Write(left, "moi.txt", "moi");
        var o = new SyncSession("heal", left, right, SyncMode.BackupOneWay, store).Run();
        Check("State mat + chi co lenh chep: van chay, chep file moi",
            o.StateLost && o.Result?.Aborted == false && File.Exists(Full(right, "moi.txt")));
        var again = new SyncSession("heal", left, right, SyncMode.BackupOneWay, store).AnalyzeOnly();
        Check("Lan sau: state da duoc dung lai, het StateLost", !again.StateLost && store.LoadStateFileCount("heal") == 6,
            $"stateLost={again.StateLost} n={store.LoadStateFileCount("heal")}");
    }

    /// <summary>Doi folder (UpsertJob xoa state cu) la xoa CO CHU Y -> khong duoc bao mat state.</summary>
    private static void RootChangeClearsMarker()
    {
        var root = NewTempDir("roots");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R"); var right2 = Path.Combine(root, "R2");
        for (int i = 0; i < 4; i++) Write(left, $"f{i}.txt", "v");
        Directory.CreateDirectory(right2);
        Write(right2, "thua.txt", "x");
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        store.UpsertJob("rt", "rt", left, right, SyncMode.MirrorOneWay, ConflictPolicy.NewerWins);
        new SyncSession("rt", left, right, SyncMode.MirrorOneWay, store).Run();
        store.UpsertJob("rt", "rt", left, right2, SyncMode.MirrorOneWay, ConflictPolicy.NewerWins);
        var o = new SyncSession("rt", left, right2, SyncMode.MirrorOneWay, store).AnalyzeOnly();
        Check("Doi folder Phai: state cu bi xoa co chu y -> khong bao StateLost", !o.StateLost && store.LoadStateFileCount("rt") is null);
    }

    /// <summary>DB cua ban cu (chua co cot state_files): migration dem file_state hien co -> Job cu duoc bao ve ngay.</summary>
    private static void MigrationInitializesMarker()
    {
        var root = NewTempDir("migrate");
        var db = Path.Combine(root, "s.db");
        Exec(db, """
            CREATE TABLE jobs (id TEXT PRIMARY KEY, name TEXT NOT NULL, left_root TEXT NOT NULL, right_root TEXT NOT NULL,
                mode INTEGER NOT NULL, conflict_policy INTEGER NOT NULL DEFAULT 0, settings_json TEXT NOT NULL DEFAULT '{}');
            CREATE TABLE file_state (job_id TEXT NOT NULL, rel_path TEXT NOT NULL, size INTEGER NOT NULL,
                mtime_utc_ticks INTEGER NOT NULL, hash TEXT NULL, PRIMARY KEY (job_id, rel_path));
            INSERT INTO jobs (id, name, left_root, right_root, mode) VALUES ('cu', 'cu', 'C:\L', 'C:\R', 1), ('rong', 'rong', 'C:\L2', 'C:\R2', 1);
            INSERT INTO file_state VALUES ('cu', 'a.txt', 1, 0, NULL), ('cu', 'b.txt', 1, 0, NULL), ('cu', 'c.txt', 1, 0, NULL);
            """);
        using var store = new SqliteStateStore(db);
        Check("Migration DB cu: state_files = so file_state hien co (3); Job chua co state = null",
            store.LoadStateFileCount("cu") == 3 && store.LoadStateFileCount("rong") is null,
            $"cu={store.LoadStateFileCount("cu")} rong={store.LoadStateFileCount("rong")}");
    }

    /// <summary>H1a: state.db hong -> constructor nem loi nhung PHAI nha file (app doi ten file hong roi tao DB moi).
    /// Truoc khi sua: ket noi mo bi bo roi giu khoa -> File.Move loi "dang duoc dung" -> app crash (do tren app that).</summary>
    private static void CorruptDbReleasesFile()
    {
        var root = NewTempDir("corruptdb");
        var db = Path.Combine(root, "state.db");
        var junk = new byte[3000];
        new Random(7).NextBytes(junk);
        File.WriteAllBytes(db, System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(junk).ToArray());
        Exception? error = null;
        try { using var s = new SqliteStateStore(db); } catch (Exception ex) { error = ex; }
        SqliteConnection.ClearAllPools();
        bool moved;
        try { File.Move(db, db + ".hong"); moved = true; } catch (IOException) { moved = false; }
        Check("DB hong: mo nem SqliteException va nha file (doi ten duoc ngay)", error is SqliteException && moved,
            $"loi={error?.GetType().Name} doi ten={moved}");
        using var fresh = new SqliteStateStore(db);
        Check("DB hong: tao DB moi cung duong dan sau khi doi ten", fresh.ListJobs().Count == 0);
    }

    private static void CorruptSettings()
    {
        var good = new JobOptions { ExcludePatterns = "giu/*", SafetyMaxDeletePercent = 10 }.ToJson();
        var bad = JobOptions.FromJson(good[..(good.Length / 2)]);
        Check("settings_json cat cut -> SettingsCorrupt = true", bad.SettingsCorrupt);
        Check("JSON hop le / rong / '{}' -> khong bi danh dau hong",
            !JobOptions.FromJson(good).SettingsCorrupt && !JobOptions.FromJson("").SettingsCorrupt && !JobOptions.FromJson("{}").SettingsCorrupt);
        Check("Co SettingsCorrupt KHONG duoc ghi vao JSON (Luu lai Tuy chon = het hong)",
            !bad.ToJson().Contains("SettingsCorrupt") && !JobOptions.FromJson(bad.ToJson()).SettingsCorrupt);

        // JobRunner tu choi chay (ca Phan tich lan Dong bo), bao loi ro rang
        var root = NewTempDir("corrupt");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        Write(left, "a.txt", "a");
        Write(right, "giu/quan-trong.txt", "x");
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        store.UpsertJob("bad", "bad", left, right, SyncMode.MirrorOneWay, ConflictPolicy.NewerWins, good[..(good.Length / 2)]);
        using var runner = new JobRunner(store.ListJobs().Single(), store);
        JobRunReport? report = null;
        runner.Finished += (_, r) => report = r;
        var outcome = runner.RunAsync("thu cong").GetAwaiter().GetResult();
        Check("JobRunner: cau hinh hong -> khong chay, loi ro rang, file o Phai nguyen ven",
            outcome is null && report?.Error is InvalidDataException && File.Exists(Full(right, "giu/quan-trong.txt"))
            && !File.Exists(Full(right, "a.txt")),
            $"loi={report?.Error?.GetType().Name}: {report?.Error?.Message}");
        Exception? analyzeError = null;
        try { runner.AnalyzeAsync().GetAwaiter().GetResult(); } catch (Exception ex) { analyzeError = ex; }
        Check("JobRunner: Phan tich cung bao loi cau hinh hong", analyzeError is InvalidDataException,
            analyzeError?.GetType().Name ?? "khong loi");
    }
}
