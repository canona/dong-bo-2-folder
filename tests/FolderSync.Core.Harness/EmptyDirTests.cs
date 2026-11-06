using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Dong bo THU MUC RONG (JobOptions.SyncEmptyFolders / SyncSession syncEmptyDirectories) - di qua duong that
// SyncSession.Run + FolderScanner + SQLite dir_state tren thu muc tam. Moi kich ban kiem them: lan phan tich
// sau = 0 hanh dong (hoi tu, khong lap tao/xoa moi lan chay).
public static class EmptyDirTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] DIR {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-dir-{label}-{Guid.NewGuid():N}");
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

    private static bool Dir(string root, string rel) => Directory.Exists(Full(root, rel));
    private static void MkDir(string root, string rel) => Directory.CreateDirectory(Full(root, rel));

    private static List<SyncAction> Work(IEnumerable<SyncAction> actions)
        => actions.Where(a => a.Kind is not (SyncActionKind.Skip or SyncActionKind.Conflict)).ToList();

    private static string Show(IEnumerable<SyncAction> actions)
        => string.Join("; ", actions.Select(a => $"{a.Kind}{(a.IsDirectory ? "[dir]" : "")} {a.RelativePath}"));

    private sealed class Env(string label, SyncMode mode, bool propagate = false) : IDisposable
    {
        public readonly string Root = NewTempDir(label);
        public string L => Path.Combine(Root, "L");
        public string R => Path.Combine(Root, "R");
        private SqliteStateStore? _store;
        public SqliteStateStore Store => _store ??= new SqliteStateStore(Path.Combine(Root, "s.db"));
        public bool SyncDirs = true;

        public SyncSession Session() => new(label, L, R, mode, Store, new SyncOptions(), ConflictPolicy.NewerWins,
            backupPropagateDeletes: propagate, syncEmptyDirectories: SyncDirs);

        public SessionOutcome Run() => Session().Run();
        public List<SyncAction> PendingWork() => Work(Session().AnalyzeOnly().Actions);

        public void Dispose() => _store?.Dispose();
    }

    private static void Converged(string name, Env env)
    {
        var pending = env.PendingWork();
        Check($"{name}: lan phan tich sau = 0 hanh dong", pending.Count == 0, Show(pending));
    }

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- DIR: dong bo thu muc rong ---");
        BackupCreatesEmptyDirs();
        MirrorRemovesExtraEmptyDirs();
        TwoWayCreateAndDelete();
        TwoWayFileDeleteKeepsDir();
        NoDirStateNeverDeletes();
        RootMissingBlocksAndNeverCreatesRoot();
        FileReplacesEmptyDir();
        FileReplacesDirWithEmptyLeftovers();
        DirReplacesFile();
        DisabledTouchesNothing();
        ContinuousCreatesDirOnTarget();
        return (_pass, _fail);
    }

    private static void BackupCreatesEmptyDirs()
    {
        using var env = new Env("backup", SyncMode.BackupOneWay);
        Write(env.L, "f.txt", "1");
        MkDir(env.L, "a/b/c");
        MkDir(env.L, "rong");
        MkDir(env.R, "chi-o-dich");
        var outcome = env.Run();
        var dirActs = outcome.Actions.Where(a => a.IsDirectory).ToList();
        Check("Backup: thu muc rong long nhau a/b/c + 'rong' duoc tao o dich",
            Dir(env.R, "a/b/c") && Dir(env.R, "rong"), Show(dirActs));
        Check("Backup: chi sinh lenh cho thu muc la (khong lap a, a/b)",
            dirActs.Count == 2 && dirActs.All(a => a.Kind == SyncActionKind.CopyLeftToRight), Show(dirActs));
        Check("Backup: thu muc rong chi co o dich van con", Dir(env.R, "chi-o-dich"));
        Check("Backup: nguon giu nguyen", Dir(env.L, "a/b/c") && Dir(env.L, "rong"));
        Converged("Backup", env);

        // Xoa thu muc o nguon: Backup khong lan truyen xoa
        Directory.Delete(Full(env.L, "rong"));
        env.Run();
        Check("Backup: xoa thu muc o nguon -> dich giu nguyen", Dir(env.R, "rong"));
        Converged("Backup sau xoa o nguon", env);
        // Xoa o dich -> tao lai tu nguon (nhu file)
        Directory.Delete(Full(env.R, "a/b/c"));
        env.Run();
        Check("Backup: thu muc o dich bi xoa -> tao lai tu nguon", Dir(env.R, "a/b/c"));

        using var prop = new Env("backup-prop", SyncMode.BackupOneWay, propagate: true);
        MkDir(prop.L, "x");
        Write(prop.L, "f.txt", "1");
        prop.Run();
        Directory.Delete(Full(prop.L, "x"));
        prop.Run();
        Check("Backup + lan truyen xoa: thu muc da dong bo bi xoa o nguon -> xoa o dich", !Dir(prop.R, "x"));
        Converged("Backup + lan truyen xoa", prop);
    }

    private static void MirrorRemovesExtraEmptyDirs()
    {
        using var env = new Env("mirror", SyncMode.MirrorOneWay);
        Write(env.L, "f.txt", "1");
        MkDir(env.L, "giu");
        MkDir(env.R, "thua/sau/nua");
        MkDir(env.R, "thua2");
        Write(env.R, "co-file-loc/Thumbs.db", "x"); // chi chua file bi loc -> khong rong that
        env.Run();
        Check("Mirror: thu muc rong thua o dich bi xoa ca cay", !Dir(env.R, "thua") && !Dir(env.R, "thua2"));
        Check("Mirror: thu muc rong o nguon duoc tao o dich", Dir(env.R, "giu"));
        Check("Mirror: thu muc chi chua file bi loc KHONG bi xoa", File.Exists(Full(env.R, "co-file-loc/Thumbs.db")));
        Converged("Mirror", env);
    }

    private static void TwoWayCreateAndDelete()
    {
        using var env = new Env("twoway", SyncMode.TwoWay);
        Write(env.L, "f.txt", "1");
        MkDir(env.L, "tu-trai");
        MkDir(env.R, "tu-phai/con");
        env.Run();
        Check("2 chieu: thu muc moi 2 phia duoc tao o phia kia", Dir(env.R, "tu-trai") && Dir(env.L, "tu-phai/con"));
        Converged("2 chieu tao", env);

        Directory.Delete(Full(env.L, "tu-phai"), recursive: true);
        Directory.Delete(Full(env.R, "tu-trai"));
        var outcome = env.Run();
        Check("2 chieu: xoa thu muc o 1 phia -> phia kia cung xoa (ca cha rong)",
            !Dir(env.R, "tu-phai") && !Dir(env.L, "tu-trai"), Show(Work(outcome.Actions)));
        Check("2 chieu: file khong bi dung toi", File.ReadAllText(Full(env.R, "f.txt")) == "1");
        Converged("2 chieu xoa", env);

        // Thu muc bi xoa o Phai nhung o Trai vua co file moi ben trong -> khong xoa, file duoc chep
        MkDir(env.L, "dang-dung");
        env.Run();
        Directory.Delete(Full(env.R, "dang-dung"));
        Write(env.L, "dang-dung/moi.txt", "moi");
        env.Run();
        Check("2 chieu: Phai xoa thu muc, Trai vua them file vao -> giu thu muc + chep file",
            File.Exists(Full(env.L, "dang-dung/moi.txt")) && File.Exists(Full(env.R, "dang-dung/moi.txt")));
        Converged("2 chieu thu muc co file moi", env);
    }

    /// <summary>Xoa file cuoi cung trong 'a/' o Trai nhung giu thu muc 'a' -> Phai xoa file, KHONG xoa thu muc 'a'
    /// (truoc day don cha rong o Phai, lan sau thanh "Trai co a, Phai khong" = lap).</summary>
    private static void TwoWayFileDeleteKeepsDir()
    {
        using var env = new Env("twoway-file", SyncMode.TwoWay);
        for (int i = 0; i < 4; i++) Write(env.L, $"dem{i}.txt", "dem"); // chot 50%
        Write(env.L, "a/x.txt", "x");
        env.Run();
        File.Delete(Full(env.L, "a/x.txt"));
        env.Run();
        Check("2 chieu: xoa file cuoi trong thu muc -> file bi xoa o Phai, thu muc 'a' con o ca 2 phia",
            !File.Exists(Full(env.R, "a/x.txt")) && Dir(env.R, "a") && Dir(env.L, "a"));
        Converged("2 chieu xoa file giu thu muc", env);

        // Xoa ca thu muc o Trai -> Phai xoa file + don thu muc
        using var env2 = new Env("twoway-tree", SyncMode.TwoWay);
        for (int i = 0; i < 4; i++) Write(env2.L, $"dem{i}.txt", "dem");
        Write(env2.L, "b/y.txt", "y");
        env2.Run();
        Directory.Delete(Full(env2.L, "b"), recursive: true);
        env2.Run();
        Check("2 chieu: xoa ca thu muc co file o Trai -> Phai mat ca thu muc", !Dir(env2.R, "b"));
        Converged("2 chieu xoa ca cay", env2);
    }

    /// <summary>Job cu (chua co dir_state) / lan dau: thu muc chi co o 1 phia luon duoc TAO, khong bao gio xoa.</summary>
    private static void NoDirStateNeverDeletes()
    {
        using var env = new Env("nostate", SyncMode.TwoWay);
        Write(env.L, "f.txt", "1");
        env.SyncDirs = false;
        env.Run(); // Job "cu": co file_state, chua co dir_state
        MkDir(env.R, "co-tu-truoc");
        MkDir(env.L, "cung-co-tu-truoc");
        env.SyncDirs = true;
        var acts = Work(env.Run().Actions);
        Check("Chua co dir_state: thu muc 1 phia duoc tao o phia kia, khong co lenh xoa",
            Dir(env.L, "co-tu-truoc") && Dir(env.R, "cung-co-tu-truoc")
            && !acts.Any(a => a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight), Show(acts));
    }

    private static void RootMissingBlocksAndNeverCreatesRoot()
    {
        using var env = new Env("rootmiss", SyncMode.TwoWay);
        Write(env.L, "f.txt", "1");
        MkDir(env.L, "rong1");
        MkDir(env.L, "rong2");
        env.Run();
        Directory.Delete(env.R, recursive: true); // "USB bi rut"
        var outcome = env.Run();
        Check("2 chieu, goc Phai mat: chot RootMissing chan, thu muc rong o Trai con nguyen",
            outcome.Result?.Aborted == true && Dir(env.L, "rong1") && Dir(env.L, "rong2"),
            $"aborted={outcome.Result?.Aborted}, {Show(Work(outcome.Actions))}");
        Check("2 chieu, goc Phai mat: khong tao lai folder goc", !Directory.Exists(env.R));

        using var bk = new Env("rootmiss-bk", SyncMode.BackupOneWay);
        Write(bk.L, "f.txt", "1");
        bk.Run();
        Directory.Delete(bk.R, recursive: true);
        File.Delete(Full(bk.L, "f.txt")); // chi con lenh thu muc - tach khoi hanh vi chep file
        MkDir(bk.L, "chi-thu-muc");
        bk.Run();
        Check("Backup, goc dich mat: lenh tao thu muc KHONG tao lai folder goc", !Directory.Exists(bk.R));
    }

    /// <summary>Thu muc rong o dich chan duong file cung ten tu nguon -> xoa thu muc, chep file trong 1 lan chay.</summary>
    private static void FileReplacesEmptyDir()
    {
        foreach (var mode in new[] { SyncMode.BackupOneWay, SyncMode.MirrorOneWay })
        {
            using var env = new Env("file-over-dir-" + mode, mode);
            Write(env.L, "a", "file a");
            MkDir(env.R, "a");
            var outcome = env.Run();
            Check($"{mode}: thu muc rong 'a' o dich -> thay bang file 'a' trong 1 lan chay",
                File.Exists(Full(env.R, "a")) && File.ReadAllText(Full(env.R, "a")) == "file a",
                $"{Show(Work(outcome.Actions))} | loi: {string.Join("; ", outcome.Result?.Errors ?? new())}");
            Converged($"{mode} file thay thu muc", env);
        }
    }

    /// <summary>Fuzz HARD-A1 (2.000 buoc, 2 chieu) bat duoc: Trai doi thu muc 'd' (co file) thanh file 'd'; Phai xoa
    /// file trong 'd/' nhung con thu muc con RONG sot lai -> chep file 'd' loi I/O mai. Ban &lt;= 0.3.1 nho PruneEmptyDirs
    /// don toan cay; nay chi don dung cay rong dang chan lenh chep. Chay ca khi tat dong bo thu muc rong.</summary>
    private static void FileReplacesDirWithEmptyLeftovers()
    {
        foreach (bool syncDirs in new[] { false, true })
        {
            using var env = new Env("dir2file-left-" + syncDirs, SyncMode.TwoWay) { SyncDirs = syncDirs };
            for (int i = 0; i < 6; i++) Write(env.L, $"dem{i}.txt", "dem");
            Write(env.L, "d/x.txt", "x");
            Write(env.L, "d/con/y.txt", "y");
            env.Run();
            MkDir(env.R, "d/sot-lai/rong"); // thu muc rong sot o Phai (truoc day bi don toan cay)
            Directory.Delete(Full(env.L, "d"), recursive: true);
            Write(env.L, "d", "gio la file");
            var outcome = env.Run();
            Check($"2 chieu (dong bo thu muc rong={syncDirs}): thu muc -> file, Phai con thu muc con rong -> Phai thanh file 'd'",
                File.Exists(Full(env.R, "d")) && File.ReadAllText(Full(env.R, "d")) == "gio la file"
                && (outcome.Result?.Errors.Count ?? 0) == 0,
                $"{Show(Work(outcome.Actions))} | loi: {string.Join("; ", outcome.Result?.Errors ?? new())}");
            Converged($"2 chieu thu muc -> file (dong bo thu muc rong={syncDirs})", env);
        }

        // Thu muc dich con FILE (ke ca file bi loc) -> KHONG duoc xoa: bao loi, du lieu nguyen ven
        using var keep = new Env("dir2file-keep", SyncMode.BackupOneWay);
        Write(keep.L, "d", "file nguon");
        Write(keep.R, "d/Thumbs.db", "file bi loc nhung van la du lieu");
        keep.Run();
        Check("Backup: thu muc o dich con file (bi loc) -> khong bi xoa de chep file trung ten",
            File.Exists(Full(keep.R, "d/Thumbs.db")));
    }

    private static void DirReplacesFile()
    {
        using var env = new Env("dir-over-file", SyncMode.MirrorOneWay);
        for (int i = 0; i < 4; i++) Write(env.L, $"dem{i}.txt", "dem");
        MkDir(env.L, "a/b");
        Write(env.R, "a", "file thua");
        var outcome = env.Run();
        Check("Mirror: file 'a' o dich + thu muc rong 'a/b' o nguon -> dich thanh thu muc",
            Dir(env.R, "a/b") && !File.Exists(Full(env.R, "a")),
            $"{Show(Work(outcome.Actions))} | loi: {string.Join("; ", outcome.Result?.Errors ?? new())}");
        Converged("Mirror thu muc thay file", env);

        using var bk = new Env("dir-over-file-bk", SyncMode.BackupOneWay);
        MkDir(bk.L, "a/b");
        Write(bk.R, "a", "file cua dich");
        var o2 = bk.Run();
        Check("Backup: file 'a' chi co o dich duoc giu, khong bao loi lap lai",
            File.ReadAllText(Full(bk.R, "a")) == "file cua dich" && (o2.Result?.Errors.Count ?? 0) == 0,
            string.Join("; ", o2.Result?.Errors ?? new()));
    }

    private static void DisabledTouchesNothing()
    {
        using var env = new Env("disabled", SyncMode.MirrorOneWay) { SyncDirs = false };
        Write(env.L, "f.txt", "1");
        MkDir(env.L, "rong-nguon");
        MkDir(env.R, "rong-dich");
        env.Run();
        Check("Tat dong bo thu muc rong: khong tao, khong xoa thu muc rong o ca 2 phia",
            Dir(env.L, "rong-nguon") && !Dir(env.R, "rong-nguon") && Dir(env.R, "rong-dich"));
    }

    /// <summary>JobRunner lien tuc (mac dinh SyncEmptyFolders): tao thu muc rong o nguon -> tu xuat hien o dich.</summary>
    private static void ContinuousCreatesDirOnTarget()
    {
        var root = NewTempDir("auto");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        Write(left, "f.txt", "1");
        Directory.CreateDirectory(right);
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        var o = new JobOptions { AutoOnFileChange = true, AutoFileChangeDelaySec = 1, CleanupHistory = false };
        Check("JobOptions mac dinh bat dong bo thu muc rong (ca Job cu khong co truong trong JSON)",
            o.SyncEmptyFolders && JobOptions.FromJson("{\"AutoOnFileChange\":true}").SyncEmptyFolders);
        store.UpsertJob("auto-dir", "auto-dir", left, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins, o.ToJson());
        using var runner = new JobRunner(store.ListJobs().Single(), store);
        runner.StartAuto(appStarting: true);
        bool first = WaitUntil(() => File.Exists(Full(right, "f.txt")) && !runner.IsBusy);
        int runs = runner.RunCount;
        Directory.CreateDirectory(Full(left, "Thu muc lien tuc"));
        Check("Lien tuc: tao thu muc rong o nguon -> tu xuat hien o dich",
            first && WaitUntil(() => Dir(right, "Thu muc lien tuc"), 10000),
            $"lan dau: {first}, so lan chay: {runs} -> {runner.RunCount}");
        // Thu muc do app tao khong duoc kich hoat vong chay vo han
        WaitUntil(() => !runner.IsBusy, 5000);
        int settled = runner.RunCount;
        Thread.Sleep(3000);
        Check("Lien tuc: thu muc app vua tao o dich khong tu kich hoat chay lai lien tuc",
            runner.RunCount - settled <= 1, $"{settled} -> {runner.RunCount}");
        runner.StopAuto();
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            Thread.Sleep(100);
        }
        return cond();
    }
}
