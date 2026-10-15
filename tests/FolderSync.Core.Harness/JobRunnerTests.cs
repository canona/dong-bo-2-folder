using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung JobRunner: dong bo lien tuc (watcher that), khong tu kich hoat bang file chinh no ghi,
// cac Job doc lap, 1 Job khong chay chong, cache hash, store an toan da luong.
public static class JobRunnerTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] RUN {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-run-{label}-{Guid.NewGuid():N}");
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

    private static string? Read(string root, string rel)
    {
        try { return File.Exists(Full(root, rel)) ? File.ReadAllText(Full(root, rel)) : null; }
        catch (IOException) { return null; } // dang bi ghi do
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

    private static (JobRunner runner, SqliteStateStore store) NewRunner(string label, string left, string right,
        SyncMode mode, JobOptions o)
    {
        var store = new SqliteStateStore(Path.Combine(NewTempDir(label + "-db"), "s.db"));
        var id = "job-" + label + "-" + Guid.NewGuid().ToString("N")[..6];
        store.UpsertJob(id, label, left, right, mode, ConflictPolicy.NewerWins, o.ToJson());
        return (new JobRunner(store.ListJobs().Single(), store), store);
    }

    private static JobOptions Continuous() => new() { AutoOnFileChange = true, AutoFileChangeDelaySec = 1, CleanupHistory = false };

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- RUN: JobRunner / dong bo lien tuc / Job doc lap ---");
        ContinuousBackup();
        ContinuousMirrorRestoresTarget();
        ContinuousMirrorCreatesTarget();
        NoSelfTriggerLoop();
        IndependentJobs();
        ReplacedRunnerWhileRunning();
        ReplacedRunnerCancel();
        PauseResumeCancel();
        HashCache();
        StoreThreadSafety();
        return (_pass, _fail);
    }

    // --- Backup lien tuc: them / sua file o nguon -> tu chep sang dich ---
    private static void ContinuousBackup()
    {
        var left = NewTempDir("cb-l"); var right = NewTempDir("cb-r");
        Write(left, "co-san.txt", "1");
        var (runner, store) = NewRunner("cb", left, right, SyncMode.BackupOneWay, Continuous());
        using (store) using (runner)
        {
            runner.StartAuto(appStarting: true);
            Check("Lien tuc: bat len thi chay bat kip 1 lan (file co san duoc chep)",
                WaitUntil(() => Read(right, "co-san.txt") == "1"));

            Write(left, "thu muc/moi.txt", "moi");
            Check("Lien tuc Backup: tao file moi o nguon -> tu xuat hien o dich",
                WaitUntil(() => Read(right, "thu muc/moi.txt") == "moi"));

            Write(left, "co-san.txt", "da sua");
            Check("Lien tuc Backup: sua file o nguon -> dich duoc cap nhat",
                WaitUntil(() => Read(right, "co-san.txt") == "da sua"));

            // Nguoi dung xoa file o DICH -> Backup phai chep lai tu nguon (truoc day chi theo doi nguon)
            File.Delete(Full(right, "thu muc/moi.txt"));
            File.Delete(Full(right, "co-san.txt"));
            Check("Lien tuc Backup: xoa file o dich -> tu chep lai tu nguon",
                WaitUntil(() => Read(right, "thu muc/moi.txt") == "moi" && Read(right, "co-san.txt") == "da sua"));

            // File them o dich: Backup giu nguyen, khong xoa
            Write(right, "chi-o-dich.txt", "x");
            Thread.Sleep(3500);
            Check("Lien tuc Backup: file chi co o dich duoc giu nguyen", Read(right, "chi-o-dich.txt") == "x");
        }
    }

    // --- Mirror lien tuc: ai do sua/them o dich -> tra ve giong nguon ---
    private static void ContinuousMirrorRestoresTarget()
    {
        var left = NewTempDir("cm-l"); var right = NewTempDir("cm-r");
        for (int i = 0; i < 4; i++) Write(left, $"f{i}.txt", "goc" + i);
        var (runner, store) = NewRunner("cm", left, right, SyncMode.MirrorOneWay, Continuous());
        using (store) using (runner)
        {
            runner.StartAuto(appStarting: false);
            WaitUntil(() => Read(right, "f3.txt") == "goc3");
            Write(right, "f0.txt", "bi sua o dich");
            Check("Lien tuc Mirror: sua file o dich -> tra ve noi dung nguon",
                WaitUntil(() => Read(right, "f0.txt") == "goc0"));
            Write(left, "f1.txt", "nguon doi");
            Check("Lien tuc Mirror: sua o nguon -> sang dich",
                WaitUntil(() => Read(right, "f1.txt") == "nguon doi"));
        }
    }

    // --- Mirror lien tuc khi folder dich CHUA ton tai: lan chay dau tao folder -> gan watcher ngay ---
    private static void ContinuousMirrorCreatesTarget()
    {
        var left = NewTempDir("cc-l"); var right = Path.Combine(NewTempDir("cc-p"), "dich-chua-co");
        Write(left, "a.txt", "goc");
        var o = Continuous(); o.CreateFoldersIfMissing = true;
        var (runner, store) = NewRunner("cc", left, right, SyncMode.MirrorOneWay, o);
        using (store) using (runner)
        {
            runner.StartAuto(appStarting: true);
            WaitUntil(() => Read(right, "a.txt") == "goc");
            Write(right, "a.txt", "sua o dich ngay sau khi tao");
            Check("Lien tuc Mirror, dich chua co: tao folder roi theo doi NGAY (khong doi chu ky 15 giay)",
                WaitUntil(() => Read(right, "a.txt") == "goc", 8000), Read(right, "a.txt") ?? "(null)");
        }
    }

    // --- File do chinh lan chay ghi ra khong duoc kich hoat lai Job (vong lap vo han) ---
    private static void NoSelfTriggerLoop()
    {
        var left = NewTempDir("ns-l"); var right = NewTempDir("ns-r");
        for (int i = 0; i < 20; i++) Write(left, $"sub{i % 3}/f{i}.txt", new string('x', 1000 + i));
        var (runner, store) = NewRunner("ns", left, right, SyncMode.TwoWay, Continuous());
        using (store) using (runner)
        {
            runner.StartAuto(appStarting: false);
            WaitUntil(() => Directory.Exists(right) && Directory.GetFiles(right, "*", SearchOption.AllDirectories).Length == 20);
            WaitUntil(() => !runner.IsBusy && runner.RunCount >= 1);
            int after = runner.RunCount;
            Thread.Sleep(5000); // > tre debounce + thoi gian an han
            Check("Lien tuc 2 chieu: chep 20 file xong KHONG tu kich hoat lai (khong vong lap)",
                runner.RunCount == after, $"RunCount {after} -> {runner.RunCount}");
        }
    }

    // --- 2 Job chay song song; 1 Job khong chay 2 phien chong nhau ---
    private static void IndependentJobs()
    {
        var aL = NewTempDir("ia-l"); var aR = NewTempDir("ia-r");
        File.WriteAllBytes(Full(aL, "lon.bin"), new byte[64 * 1024 * 1024]); // du lau de Job A con dang chay khi goi lan 2
        var bL = NewTempDir("ib-l"); var bR = NewTempDir("ib-r");
        Write(bL, "b.txt", "b");
        var (a, storeA) = NewRunner("ia", aL, aR, SyncMode.BackupOneWay, new JobOptions());
        var (b, storeB) = NewRunner("ib", bL, bR, SyncMode.BackupOneWay, new JobOptions());
        using (storeA) using (storeB) using (a) using (b)
        {
            var runA = a.RunAsync("test");
            WaitUntil(() => a.IsBusy, 5000);
            var second = a.RunAsync("test-chong").GetAwaiter().GetResult();
            Check("Cung 1 Job: yeu cau chay thu 2 khi dang chay bi tu choi (null)", second is null);

            var resB = b.RunAsync("test").GetAwaiter().GetResult();
            Check("Job khac van chay duoc trong luc Job A dang chay (doc lap)",
                resB?.Result is { Aborted: false, Copied: 1 } && Read(bR, "b.txt") == "b",
                $"B={resB?.Result?.Copied} A dang chay={a.IsBusy}");

            var resA = runA.GetAwaiter().GetResult();
            Check("Job A van hoan tat dung sau do", resA?.Result?.Copied == 1);
        }
    }

    // --- Sua Job (doi tuy chon / bat-tat lien tuc) trong luc dang chay = runner MOI thay runner cu, runner cu
    //     van chep tiep (khoa theo Id). Truoc day runner moi bao "ranh": UI an nut Huy + thanh tien trinh,
    //     va thay doi file trong luc runner cu chay bi bo qua (runner moi gap khoa ban -> bo luon). ---
    private static void ReplacedRunnerWhileRunning()
    {
        var left = NewTempDir("rr-l"); var right = NewTempDir("rr-r");
        File.WriteAllBytes(Full(left, "lon.bin"), new byte[8 * 1024 * 1024]);
        var o = Continuous(); o.MaxCopySpeedMBps = 2; // ~4 giay -> con dang chay khi thay runner
        var (a, store) = NewRunner("rr", left, right, SyncMode.BackupOneWay, o);
        using (store)
        {
            var planned = new StageFlag(SyncProgressStage.Bytes);
            var runA = a.RunAsync("test", planned);
            WaitUntil(() => planned.Seen, 5000); // runner cu da quet xong + dang chep -> khong thay file ghi sau
            var b = new JobRunner(store.ListJobs().Single(), store);
            b.InheritHistory(a);
            a.Dispose();
            using (b)
            {
                b.StartAuto(appStarting: false);
                Check("Thay runner khi dang chay: runner moi bao dang ban (UI con nut Huy)",
                    b.IsBusy && b.State == JobRunState.Running, $"IsBusy={b.IsBusy} State={b.State}");
                Write(left, "moi.txt", "trong luc runner cu chay");
                runA.GetAwaiter().GetResult();
                Check("Thay runner khi dang chay: runner cu xong -> runner moi het ban", WaitUntil(() => !b.IsBusy, 3000));
                Check("Thay runner khi dang chay: file doi trong luc runner cu chay van duoc dong bo sau do",
                    WaitUntil(() => Read(right, "moi.txt") == "trong luc runner cu chay", 20000));
            }
        }
    }

    private static void ReplacedRunnerCancel()
    {
        var left = NewTempDir("rc-l"); var right = NewTempDir("rc-r");
        File.WriteAllBytes(Full(left, "lon.bin"), new byte[16 * 1024 * 1024]);
        var o = new JobOptions { MaxCopySpeedMBps = 2, CleanupHistory = false }; // ~8 giay neu khong huy
        var (a, store) = NewRunner("rc", left, right, SyncMode.BackupOneWay, o);
        using (store)
        {
            var runA = a.RunAsync("test");
            WaitUntil(() => a.IsBusy, 5000);
            var b = new JobRunner(store.ListJobs().Single(), store);
            a.Dispose();
            using (b)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                b.Cancel(); // nut Huy tren UI goi runner HIEN TAI
                var res = runA.Wait(4000) ? runA.Result : null;
                Check("Thay runner khi dang chay: Huy tu runner moi dung duoc lan chay cua runner cu",
                    res?.Result?.Canceled == true, $"{sw.ElapsedMilliseconds} ms, Canceled={res?.Result?.Canceled}");
                if (res is null) runA.Wait();
                Check("Thay runner + Huy: khong sot file tam",
                    Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Length == 0);
            }
        }
    }

    // --- Tam dung / tiep tuc / huy khi dang tam dung: dung giua 2 khoi ghi file tam, khong dong vao file dich ---
    private static void PauseResumeCancel()
    {
        var left = NewTempDir("pz-l"); var right = NewTempDir("pz-r");
        var data = new byte[6 * 1024 * 1024];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Full(left, "lon.bin"), data);
        File.WriteAllBytes(Full(left, "lon2.bin"), data);
        var o = new JobOptions { MaxCopySpeedMBps = 3, CleanupHistory = false };
        var (r, store) = NewRunner("pz", left, right, SyncMode.BackupOneWay, o);
        using (store) using (r)
        {
            var copying = new StageFlag(SyncProgressStage.Bytes);
            var run = r.RunAsync("test", copying);
            WaitUntil(() => copying.Seen, 5000);
            r.Pause();
            Thread.Sleep(300); // khoi dang ghi do xong
            long TmpBytes() => Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            long before = TmpBytes();
            Thread.Sleep(1500);
            long after = TmpBytes();
            Check("Tam dung: IsPaused + Job van ban (khoa giu, khong chay chong)", r.IsPaused && r.IsBusy);
            Check("Tam dung: file tam KHONG tang trong 1,5 giay, file dich chua bi dong vao",
                before == after && !File.Exists(Full(right, "lon.bin")), $"tmp {before} -> {after}");
            r.Resume();
            var res = run.Wait(15000) ? run.Result : null;
            Check("Tiep tuc: chep xong ca 2 file, dung noi dung",
                res?.Result?.Copied == 2 && !r.IsPaused
                && File.ReadAllBytes(Full(right, "lon.bin")).AsSpan().SequenceEqual(data),
                $"Copied={res?.Result?.Copied}");

            // Huy trong luc dang tam dung -> dung ngay, xoa file tam
            File.Delete(Full(right, "lon.bin")); File.Delete(Full(right, "lon2.bin"));
            copying = new StageFlag(SyncProgressStage.Bytes);
            run = r.RunAsync("test", copying);
            WaitUntil(() => copying.Seen, 5000);
            r.Pause();
            Thread.Sleep(300);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            r.Cancel();
            res = run.Wait(3000) ? run.Result : null;
            Check("Huy khi dang tam dung: dung ngay (< 3 giay), Canceled", res?.Result?.Canceled == true,
                $"{sw.ElapsedMilliseconds} ms");
            if (res is null) run.Wait();
            Check("Huy khi dang tam dung: khong sot file tam",
                Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Length == 0);
            Check("Huy khi dang tam dung: lan chay sau khong bi ket tam dung",
                r.RunAsync("test").Wait(15000) && !r.IsPaused && File.Exists(Full(right, "lon2.bin")));
        }
    }

    /// <summary>Bao (dong bo, tren thread chep) khi thay 1 giai doan tien trinh.</summary>
    private sealed class StageFlag(SyncProgressStage stage) : IProgress<SyncProgress>
    {
        public volatile bool Seen;
        public void Report(SyncProgress value) { if (value.Stage == stage) Seen = true; }
    }

    // --- Cache hash: file khong doi size + mtime thi dung lai hash cu ---
    private static void HashCache()
    {
        var root = NewTempDir("hc");
        Write(root, "a.txt", "noi dung");
        var scanner = new FolderScanner();
        var first = scanner.Scan(root, computeHashes: true).Entries;
        var fake = first.ToDictionary(kv => kv.Key, kv => kv.Value with { Hash = "HASH-CU" }, StringComparer.OrdinalIgnoreCase);
        var reused = scanner.Scan(root, computeHashes: true, fake).Entries["a.txt"].Hash;
        Check("Cache hash: file khong doi -> dung lai hash trong cache (khong doc lai file)", reused == "HASH-CU");

        File.WriteAllText(Full(root, "a.txt"), "noi dung khac dai hon");
        var fresh = scanner.Scan(root, computeHashes: true, fake).Entries["a.txt"].Hash;
        Check("Cache hash: file doi size/mtime -> tinh hash moi", fresh is not null && fresh != "HASH-CU");
    }

    // --- Nhieu Job dung chung 1 store tu nhieu thread ---
    private static void StoreThreadSafety()
    {
        using var store = new SqliteStateStore(Path.Combine(NewTempDir("ts"), "s.db"));
        var entries = Enumerable.Range(0, 50).ToDictionary(i => $"f{i}.txt",
            i => new FileEntry($"f{i}.txt", i, DateTime.UtcNow, "H" + i), StringComparer.OrdinalIgnoreCase);
        Exception? err = null;
        Parallel.For(0, 8, t =>
        {
            try
            {
                for (int k = 0; k < 40; k++)
                {
                    store.SaveState("job" + t, entries);
                    store.LoadState("job" + t);
                    store.SaveRun("job" + t, DateTime.UtcNow, DateTime.UtcNow, 1, 0, 0, 0, 0, 0);
                }
            }
            catch (Exception ex) { err = ex; }
        });
        Check("Store: 8 thread doc/ghi dong thoi khong loi", err is null && store.CountRuns("job7") == 40, err?.Message ?? "");
    }
}
