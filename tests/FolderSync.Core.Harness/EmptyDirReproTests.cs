using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Repro loi bao Dien Bien (11/2026): Backup 1 chieu "khong xoa o dich" nhung thu muc vua tao o NGUON bi xoa sau
// 1 luc. Nguyen nhan: SyncExecutor.PruneEmptyDirs xoa MOI thu muc rong o CA 2 phia cuoi moi lan chay (ke ca
// 0 hanh dong); doi ten "New folder" ban su kien Deleted cho ten cu -> Job lien tuc chay -> xoa thu muc.
// File nay CHI dung API co san tu ban 0.3.1 de chay duoc tren code cu (xac nhan FAIL truoc khi sua).
public static class EmptyDirReproTests
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

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- DIR repro: thu muc rong khong bi xoa (loi bao Dien Bien) ---");
        foreach (var mode in new[] { SyncMode.BackupOneWay, SyncMode.MirrorOneWay, SyncMode.TwoWay })
            SessionKeepsEmptyDirs(mode);
        ContinuousRenameNewFolder();
        return (_pass, _fail);
    }

    /// <summary>Duong engine (SyncSession mac dinh): thu muc rong o nguon con nguyen sau khi chay, ke ca khi
    /// lan chay co chep file. Backup: thu muc rong chi co o dich cung con nguyen ("khong xoa o dich").</summary>
    private static void SessionKeepsEmptyDirs(SyncMode mode)
    {
        var root = NewTempDir("repro");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        Write(left, "co-san.txt", "1");
        Directory.CreateDirectory(Full(left, "Thu muc moi"));
        Directory.CreateDirectory(Full(left, "cha/con rong"));
        Directory.CreateDirectory(right);
        if (mode == SyncMode.BackupOneWay) Directory.CreateDirectory(Full(right, "chi o dich"));
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        var session = new SyncSession("repro-" + mode, left, right, mode, store, new SyncOptions(), ConflictPolicy.NewerWins);

        session.Run();
        Check($"{mode}: thu muc rong vua tao o nguon con nguyen sau lan chay co chep file",
            Directory.Exists(Full(left, "Thu muc moi")) && Directory.Exists(Full(left, "cha/con rong")),
            $"Thu muc moi: {Directory.Exists(Full(left, "Thu muc moi"))}, cha/con rong: {Directory.Exists(Full(left, "cha/con rong"))}");

        session.Run(); // lan chay 0 hanh dong cung tung xoa
        Check($"{mode}: thu muc rong o nguon con nguyen sau lan chay khong co gi de lam",
            Directory.Exists(Full(left, "Thu muc moi")));
        if (mode == SyncMode.BackupOneWay)
            Check("BackupOneWay: thu muc rong chi co o dich khong bi xoa (Backup khong xoa o dich)",
                Directory.Exists(Full(right, "chi o dich")));
    }

    /// <summary>Dung kich ban nguoi dung: Job Backup lien tuc, tao "New folder" roi doi ten -> thu muc khong bi xoa.</summary>
    private static void ContinuousRenameNewFolder()
    {
        var root = NewTempDir("repro-auto");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        Write(left, "co-san.txt", "1");
        Directory.CreateDirectory(right);
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));
        var o = new JobOptions { AutoOnFileChange = true, AutoFileChangeDelaySec = 1, CleanupHistory = false };
        store.UpsertJob("repro-auto", "repro-auto", left, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins, o.ToJson());
        using var runner = new JobRunner(store.ListJobs().Single(), store);
        runner.StartAuto(appStarting: true);
        bool firstDone = WaitUntil(() => File.Exists(Full(right, "co-san.txt")) && !runner.IsBusy);

        Directory.CreateDirectory(Full(left, "New folder"));
        Thread.Sleep(300);
        Directory.Move(Full(left, "New folder"), Full(left, "Bao cao thang 11"));
        int runsBefore = runner.RunCount;
        bool ran = WaitUntil(() => runner.RunCount > runsBefore && !runner.IsBusy, 10000);
        Thread.Sleep(500);
        Check("Backup lien tuc: tao 'New folder' roi doi ten -> thu muc o nguon KHONG bi xoa",
            firstDone && Directory.Exists(Full(left, "Bao cao thang 11")),
            $"lan dau xong: {firstDone}, Job chay lai sau doi ten: {ran}, thu muc con: {Directory.Exists(Full(left, "Bao cao thang 11"))}");
        runner.StopAuto();
    }
}
