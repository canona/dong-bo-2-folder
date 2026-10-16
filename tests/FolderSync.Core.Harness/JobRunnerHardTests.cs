using System.Reflection;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Bo test kho giai doan 1b: HARD-G5 (sua Job 100 lan khong nhan doi watcher/timer) va
// HARD-D1 (4 nguon kich hoat cung luc tren 1 Job - khoa theo Id, khong 2 executor cung ghi).
// Moi cho can on dinh deu dung co che cho (barrier / chan o diem Planned / cho yen) - khong sleep doan.
public static class JobRunnerHardTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-hard-{label}-{Guid.NewGuid():N}");
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
            Thread.Sleep(50);
        }
        return cond();
    }

    /// <summary>Cho gia tri khong doi trong stableMs lien tiep (cua so settle thay cho sleep doan).</summary>
    private static int Settle(Func<int> value, int stableMs, int timeoutMs = 30000)
    {
        int last = value();
        var stableSince = DateTime.UtcNow;
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(50);
            int now = value();
            if (now != last) { last = now; stableSince = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - stableSince).TotalMilliseconds >= stableMs) break;
        }
        return last;
    }

    private static (JobRecord job, SqliteStateStore store) NewJob(string label, string left, string right,
        SyncMode mode, JobOptions o)
    {
        var store = new SqliteStateStore(Path.Combine(NewTempDir(label + "-db"), "s.db"));
        var id = "job-" + label + "-" + Guid.NewGuid().ToString("N")[..6];
        store.UpsertJob(id, label, left, right, mode, ConflictPolicy.NewerWins, o.ToJson());
        return (store.ListJobs().Single(), store);
    }

    private static T? Field<T>(JobRunner r, string name) where T : class
        => typeof(JobRunner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r) as T;

    private static bool Disposed(JobRunner r)
        => (bool)typeof(JobRunner).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;

    /// <summary>Chan lan chay o 1 giai doan (trong luc DANG GIU khoa) cho den khi test cho di; dem so lan toi giai doan do.</summary>
    private sealed class BlockingSink(SyncProgressStage stage) : IProgress<SyncProgress>
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _hits;
        public int Hits => Volatile.Read(ref _hits);
        public void Release() => _release.Set();
        public void Report(SyncProgress value)
        {
            if (value.Stage != stage) return;
            Interlocked.Increment(ref _hits);
            _release.Wait(30000); // du phong: khong bao gio treo harness
        }
    }

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- HARD 1b: sua Job nhieu lan (G5) / 4 nguon kich hoat cung luc (D1) ---");
        G5ReplaceRunner100Times();
        D1FourSourcesTwentyRounds();
        D1TenClicksAndAnalyzeDuringSync();
        return (_pass, _fail);
    }

    // --- HARD-G5: mo phong MainWindow.ReloadJobs 100 lan: old.Dispose -> new JobRunner -> InheritHistory -> StartAuto ---
    private static void G5ReplaceRunner100Times()
    {
        var left = NewTempDir("g5-l"); var right = NewTempDir("g5-r");
        Write(left, "co-san.txt", "1");
        // AutoPeriodMinutes > 0 de moi runner cung tao timer dinh ky (rieng voi watcher + debounce)
        var o = new JobOptions { AutoOnFileChange = true, AutoFileChangeDelaySec = 1, AutoPeriodMinutes = 60, CleanupHistory = false };
        var (job, store) = NewJob("g5", left, right, SyncMode.BackupOneWay, o);
        int started = 0;
        var all = new List<JobRunner>();
        void Hook(JobRunner r) => r.Started += (_, _) => Interlocked.Increment(ref started);

        using (store)
        {
            var current = new JobRunner(job, store);
            Hook(current); all.Add(current);
            current.StartAuto(appStarting: true);
            WaitUntil(() => current.RunCount >= 1 && store.CountRuns(job.Id) >= 1);

            const int Edits = 100;
            for (int i = 0; i < Edits; i++)
            {
                var old = current;
                old.Dispose(); // dung thu tu MainWindow: dispose runner cu, tao runner moi, ke thua lich su, bat tu dong
                current = new JobRunner(store.ListJobs().Single(), store);
                current.InheritHistory(old);
                Hook(current); all.Add(current);
                current.StartAuto(appStarting: false);
            }

            // Runner cuoi tu chay bat kip 1 lan (hanh vi that cua StartAuto) - doi het roi moi lay moc
            WaitUntil(() => current.RunCount >= 2 && !current.IsBusy);
            int runsBase = Settle(() => store.CountRuns(job.Id), 3500);
            int startedBase = Volatile.Read(ref started);

            Write(left, "moi.txt", "mot thay doi duy nhat"); // o goc (khong thu muc con) de watcher Linux khong bo sot
            bool arrived = WaitUntil(() => Read(right, "moi.txt") == "mot thay doi duy nhat");
            int runsAfter = Settle(() => store.CountRuns(job.Id), 3500);
            int startedAfter = Volatile.Read(ref started);

            Check($"G5 sua Job {Edits} lan roi tao dung 1 thay doi nguon -> file sang dich", arrived);
            Check($"G5 sau {Edits} lan sua: dung 1 lan chay duoc kich hoat (sync_runs {runsBase} -> {runsAfter}, Started {startedBase} -> {startedAfter})",
                runsAfter - runsBase == 1 && startedAfter - startedBase == 1,
                $"sync_runs +{runsAfter - runsBase}, Started +{startedAfter - startedBase}");

            var leaks = new List<string>();
            for (int i = 0; i < all.Count - 1; i++)
            {
                var r = all[i];
                var w = Field<List<FileSystemWatcher>>(r, "_watchers");
                if (!Disposed(r)) leaks.Add($"#{i} chua Disposed");
                if (w is null || w.Count != 0) leaks.Add($"#{i} con {w?.Count} watcher");
                foreach (var t in new[] { "_debounce", "_periodic", "_rewatch" })
                    if (Field<Timer>(r, t) is not null) leaks.Add($"#{i} con timer {t}");
            }
            Check($"G5 {all.Count - 1} runner cu da Dispose that: khong con watcher/timer nao", leaks.Count == 0,
                string.Join("; ", leaks.Take(5)) + (leaks.Count > 5 ? $" ... (+{leaks.Count - 5})" : ""));

            var wCur = Field<List<FileSystemWatcher>>(current, "_watchers");
            Check("G5 runner cuoi con dung 2 watcher (Trai + Phai) va timer dinh ky",
                wCur?.Count == 2 && Field<Timer>(current, "_periodic") is not null, $"watchers={wCur?.Count}");
            current.Dispose();
        }
    }

    // --- HARD-D1: 4 nguon (thu cong / watcher / dinh ky / khi mo app) goi RunAsync cung luc, 20 vong ---
    private static void D1FourSourcesTwentyRounds()
    {
        var left = NewTempDir("d1-l"); var right = NewTempDir("d1-r");
        var (job, store) = NewJob("d1", left, right, SyncMode.BackupOneWay, new JobOptions { CleanupHistory = false });
        string[] triggers = ["thu cong", "watcher", "dinh ky", "khi mo app"];
        const int Rounds = 20;
        var bad = new List<string>();

        using (store)
        {
            for (int round = 0; round < Rounds; round++)
            {
                for (int f = 0; f < 3; f++) Write(left, $"v{round}/f{f}.txt", $"vong {round} file {f}");
                // Vong chan: 4 lan goi tren CUNG 1 runner; vong le: 4 runner khac nhau (nhu sau khi sua Job) - khoa theo Id
                var runners = new JobRunner[4];
                var shared = new JobRunner(job, store);
                for (int k = 0; k < 4; k++) runners[k] = round % 2 == 0 ? shared : new JobRunner(job, store);

                var sink = new BlockingSink(SyncProgressStage.Planned);
                int before = store.CountRuns(job.Id);
                using var barrier = new Barrier(4);
                int done = 0;
                var tasks = Enumerable.Range(0, 4).Select(k => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    var oc = runners[k].RunAsync(triggers[k], sink).GetAwaiter().GetResult();
                    Interlocked.Increment(ref done);
                    return oc;
                })).ToArray();

                // Phien nao thang khoa se bi chan o Planned: 3 lan con lai phai tu tra ve (null) trong luc do.
                // Neu khoa hong, lan thu 2 cung toi Planned (Hits > 1) va khong tra ve -> het han cho thi nha ra.
                WaitUntil(() => sink.Hits >= 1, 10000);
                WaitUntil(() => Volatile.Read(ref done) >= 3, 5000);
                int doneWhileBlocked = Volatile.Read(ref done);
                int hitsWhileBlocked = sink.Hits;
                sink.Release();
                Task.WaitAll(tasks);

                var real = tasks.Select(t => t.Result).Where(oc => oc is not null).ToList();
                int delta = store.CountRuns(job.Id) - before;
                bool contentOk = Enumerable.Range(0, 3).All(f => Read(right, $"v{round}/f{f}.txt") == $"vong {round} file {f}");
                int tmp = Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Length;
                if (real.Count != 1 || hitsWhileBlocked != 1 || doneWhileBlocked != 3 || delta != 1
                    || real[0]!.Result?.Copied != 3 || !contentOk || tmp != 0)
                    bad.Add($"vong {round}: thuc su chay={real.Count}, executor={hitsWhileBlocked}, tu choi khi dang chan={doneWhileBlocked}, " +
                            $"sync_runs +{delta}, copied={real.FirstOrDefault()?.Result?.Copied}, noi dung dung={contentOk}, tmp={tmp}");
                foreach (var r in runners.Append(shared).Distinct()) r.Dispose();
            }

            Check($"D1 {Rounds} vong x 4 nguon: moi vong dung 1 phien chay that, 3 lan con lai bi tu choi (null), khong co executor thu 2",
                bad.Count == 0, string.Join(" | ", bad.Take(3)));

            // Hoi tu: quet doc lap 2 phia + state khop, khong sot file tam
            var scanner = new FolderScanner();
            var l = scanner.Scan(left, computeHashes: true); var rr = scanner.Scan(right, computeHashes: true);
            var state = store.LoadState(job.Id);
            bool same = l.Entries.Count == Rounds * 3 && l.Entries.Count == rr.Entries.Count
                && l.Entries.All(e => rr.Entries.TryGetValue(e.Key, out var x) && x.Size == e.Value.Size && x.Hash == e.Value.Hash);
            bool stateOk = state.Count == l.Entries.Count && state.Keys.All(k => l.Entries.ContainsKey(k));
            int tmpAll = Directory.GetFiles(left, "*.tmp-sync", SearchOption.AllDirectories).Length
                       + Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Length;
            Check("D1 trang thai cuoi: quet lai doc lap Trai = Phai (size + hash), khop state, khong sot *.tmp-sync",
                same && stateOk && tmpAll == 0,
                $"trai={l.Entries.Count} phai={rr.Entries.Count} state={state.Count} khop={same} tmp={tmpAll}");
        }
    }

    // --- HARD-D1 bo sung: bam Dong bo 10 lan lien tiep; bam Phan tich luc dang Dong bo; bam Dong bo luc dang Phan tich ---
    private static void D1TenClicksAndAnalyzeDuringSync()
    {
        var left = NewTempDir("dc-l"); var right = NewTempDir("dc-r");
        for (int i = 0; i < 5; i++) Write(left, $"f{i}.txt", "noi dung " + i);
        var (job, store) = NewJob("dc", left, right, SyncMode.BackupOneWay, new JobOptions { CleanupHistory = false });
        using (store)
        using (var r = new JobRunner(job, store))
        {
            var sink = new BlockingSink(SyncProgressStage.Planned);
            var first = r.RunAsync("bam Dong bo 1", sink);
            WaitUntil(() => sink.Hits >= 1, 10000);

            int rejected = 0;
            for (int click = 2; click <= 10; click++)
                if (r.RunAsync($"bam Dong bo {click}").GetAwaiter().GetResult() is null) rejected++;
            Check("D1 bam Dong bo 10 lan lien tiep: lan 1 chay, 9 lan sau bi tu choi (null), khong chay chong",
                rejected == 9 && sink.Hits == 1 && !first.IsCompleted, $"tu choi={rejected}/9, executor={sink.Hits}");

            var analyzeDuring = r.AnalyzeAsync().GetAwaiter().GetResult();
            bool destUntouched = !Directory.Exists(right)
                || Directory.GetFiles(right, "*", SearchOption.AllDirectories).Length == 0;
            Check("D1 bam Phan tich luc dang Dong bo: bi tu choi (null), phien dong bo van Running va dich chua bi dong vao",
                analyzeDuring is null && r.State == JobRunState.Running && r.IsBusy && destUntouched,
                $"analyze={(analyzeDuring is null ? "null" : "co ket qua")} state={r.State} destUntouched={destUntouched}");

            sink.Release();
            var res = first.GetAwaiter().GetResult();
            bool filesOk = Enumerable.Range(0, 5).All(i => Read(right, $"f{i}.txt") == "noi dung " + i);
            Check("D1 sau khi nha: phien dong bo hoan tat dung (chep 5 file), dung 1 dong sync_runs, khong sot .tmp-sync",
                res?.Result is { Aborted: false, Copied: 5 } && filesOk && store.CountRuns(job.Id) == 1
                && Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Length == 0,
                $"copied={res?.Result?.Copied} runs={store.CountRuns(job.Id)}");

            // Nguoc lai: dang Phan tich thi bam Dong bo -> tu choi, khong ghi gi; xong phan tich Job lai chay duoc
            Write(left, "g.txt", "moi sau");
            var scanBlock = new BlockingSink(SyncProgressStage.Scanning);
            var analyze = r.AnalyzeAsync(scanBlock);
            WaitUntil(() => scanBlock.Hits >= 1, 10000);
            var syncDuringAnalyze = r.RunAsync("bam Dong bo luc phan tich").GetAwaiter().GetResult();
            Check("D1 bam Dong bo luc dang Phan tich: bi tu choi (null), dich chua co file moi",
                syncDuringAnalyze is null && Read(right, "g.txt") is null && r.State == JobRunState.Analyzing,
                $"state={r.State}");
            scanBlock.Release();
            var ao = analyze.GetAwaiter().GetResult();
            Check("D1 phan tich bi chen van tra ket qua dung (1 hanh dong chep g.txt), khong ghi gi", ao is not null
                && ao.Actions.Count(a => a.Kind != SyncActionKind.Skip) == 1 && Read(right, "g.txt") is null);

            var again = r.RunAsync("sau phan tich").GetAwaiter().GetResult();
            Check("D1 sau khi nha khoa: Job lai chay duoc, hoi tu (g.txt sang dich), 2 dong sync_runs",
                again?.Result?.Copied == 1 && Read(right, "g.txt") == "moi sau" && store.CountRuns(job.Id) == 2,
                $"copied={again?.Result?.Copied} runs={store.CountRuns(job.Id)}");
        }
    }
}
