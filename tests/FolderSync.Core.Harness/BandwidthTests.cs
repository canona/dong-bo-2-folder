using System.Diagnostics;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung gioi han toc do copy (bang thong) cho tung Job: dung toc do, tinh chung ca lan chay,
// huy duoc trong luc dang cho, KHONG pha copy nguyen tu (.tmp-sync), tuong thich nguoc JobOptions.
public static class BandwidthTests
{
    private static int _pass, _fail;
    private const long MB = 1024 * 1024;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] BW {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-bw-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteBytes(string root, string rel, long size, int seed)
    {
        var full = Full(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(full, data);
    }

    private static bool NoTmp(string root) => !Directory.GetFiles(root, "*.tmp-sync", SearchOption.AllDirectories).Any();

    private static List<SyncAction> CopyAll(params string[] rels)
        => rels.Select(r => new SyncAction(SyncActionKind.CopyLeftToRight, r, "test")).ToList();

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- BW: Gioi han toc do copy (bang thong) ---");
        LimitedSingleFile();
        UnlimitedIsFast();
        LimitSpansWholeRun();
        CancelWhileThrottled();
        OptionsBackwardCompatible();
        JobRunnerAppliesLimit();
        return (_pass, _fail);
    }

    // 1. 2 MB/s, file 4 MB -> it nhat ~1,8 s; noi dung dung; khong sot file tam
    private static void LimitedSingleFile()
    {
        var left = NewTempDir("l1-l"); var right = NewTempDir("l1-r");
        WriteBytes(left, "lon.bin", 4 * MB, 1);
        var sw = Stopwatch.StartNew();
        var r = new SyncExecutor(left, right, new SyncOptions(MaxBytesPerSecond: 2 * MB)).Execute(CopyAll("lon.bin"), 1);
        sw.Stop();
        bool same = File.Exists(Full(right, "lon.bin"))
                    && File.ReadAllBytes(Full(left, "lon.bin")).AsSpan().SequenceEqual(File.ReadAllBytes(Full(right, "lon.bin")));
        Check("Gioi han 2 MB/s, file 4 MB: mat >= 1,8 s, noi dung dung, khong sot .tmp-sync",
            r.Copied == 1 && same && NoTmp(right) && sw.Elapsed.TotalSeconds >= 1.8,
            $"copied={r.Copied} same={same} {sw.Elapsed.TotalSeconds:0.00}s");
    }

    // 2. Khong gioi han (0) -> cung file do chep nhanh (doi chung)
    private static void UnlimitedIsFast()
    {
        var left = NewTempDir("l2-l"); var right = NewTempDir("l2-r");
        WriteBytes(left, "lon.bin", 4 * MB, 2);
        var sw = Stopwatch.StartNew();
        var r = new SyncExecutor(left, right, new SyncOptions(MaxBytesPerSecond: 0)).Execute(CopyAll("lon.bin"), 1);
        sw.Stop();
        Check("Khong gioi han (0): file 4 MB chep < 1,5 s",
            r.Copied == 1 && sw.Elapsed.TotalSeconds < 1.5, $"{sw.Elapsed.TotalSeconds:0.00}s");
    }

    // 3. Gioi han tinh CHUNG ca lan chay (khong reset theo tung file)
    private static void LimitSpansWholeRun()
    {
        var left = NewTempDir("l3-l"); var right = NewTempDir("l3-r");
        for (int i = 0; i < 4; i++) WriteBytes(left, $"f{i}.bin", 1 * MB, 10 + i);
        var sw = Stopwatch.StartNew();
        var r = new SyncExecutor(left, right, new SyncOptions(MaxBytesPerSecond: 2 * MB))
            .Execute(CopyAll("f0.bin", "f1.bin", "f2.bin", "f3.bin"), 4);
        sw.Stop();
        Check("4 file x 1 MB o 2 MB/s: tong >= 1,8 s (gioi han ca lan chay, khong reset tung file)",
            r.Copied == 4 && NoTmp(right) && sw.Elapsed.TotalSeconds >= 1.8, $"copied={r.Copied} {sw.Elapsed.TotalSeconds:0.00}s");
    }

    // 4. Huy trong luc dang cho (bi gioi han) -> dung nhanh, khong sot file tam
    private static void CancelWhileThrottled()
    {
        var left = NewTempDir("l4-l"); var right = NewTempDir("l4-r");
        WriteBytes(left, "lon.bin", 8 * MB, 4);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        var r = new SyncExecutor(left, right, new SyncOptions(MaxBytesPerSecond: 1 * MB))
            .Execute(CopyAll("lon.bin"), 1, null, cts.Token); // 8 MB o 1 MB/s = 8 s neu khong huy
        sw.Stop();
        Check("Huy khi dang cho gioi han: dung < 1,5 s, Canceled, khong sot .tmp-sync, khong co file dich do dang",
            r.Canceled && sw.Elapsed.TotalSeconds < 1.5 && NoTmp(right) && !File.Exists(Full(right, "lon.bin")),
            $"canceled={r.Canceled} {sw.Elapsed.TotalSeconds:0.00}s tmp={!NoTmp(right)}");
    }

    // 5. Tuong thich nguoc: JSON cu khong co truong moi + Legacy() -> khong gioi han
    private static void OptionsBackwardCompatible()
    {
        var oldJson = JobOptions.FromJson("{\"CompareChecksums\":true,\"DetectRenames\":true}");
        Check("JobOptions: JSON cu (khong co MaxCopySpeedMBps) va Legacy() -> 0 = khong gioi han; mac dinh Job moi cung 0",
            oldJson.MaxCopySpeedMBps == 0 && JobOptions.Legacy().MaxCopySpeedMBps == 0 && new JobOptions().MaxCopySpeedMBps == 0);
        var roundTrip = JobOptions.FromJson(new JobOptions { MaxCopySpeedMBps = 25 }.ToJson());
        Check("JobOptions: luu/doc lai gioi han 25 MB/s qua JSON", roundTrip.MaxCopySpeedMBps == 25);
    }

    // 6. JobRunner map gioi han cua Job vao lan dong bo that
    private static void JobRunnerAppliesLimit()
    {
        var left = NewTempDir("l6-l"); var right = NewTempDir("l6-r");
        WriteBytes(left, "lon.bin", 4 * MB, 6);
        using var store = new SqliteStateStore(Path.Combine(NewTempDir("l6-db"), "s.db"));
        store.UpsertJob("bw", "BW", left, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins,
            new JobOptions { MaxCopySpeedMBps = 2 }.ToJson());
        using var runner = new JobRunner(store.ListJobs().Single(), store);
        var sw = Stopwatch.StartNew();
        var o = runner.RunAsync("test").GetAwaiter().GetResult();
        sw.Stop();
        Check("JobRunner: Job dat 2 MB/s -> lan dong bo file 4 MB mat >= 1,8 s, ket qua dung",
            o?.Result is { Copied: 1, Aborted: false } && sw.Elapsed.TotalSeconds >= 1.8 && NoTmp(right),
            $"copied={o?.Result?.Copied} {sw.Elapsed.TotalSeconds:0.00}s");
    }
}
