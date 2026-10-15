using System.Diagnostics;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung toi uu phan tich folder lon: chi hash file mo ho (cung size, khac mtime), cache hash luu DB,
// hash tinh luc chep vao state, nhan dien doi ten O(n), tien trinh quet, Huy giua chung.
public static class PerfTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] PERF {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-perf-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Write(string root, string rel, string content, DateTime? mtime = null)
    {
        var full = Full(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (mtime is { } t) File.SetLastWriteTimeUtc(full, t);
    }

    private static SqliteStateStore NewStore(string label) => new(Path.Combine(NewTempDir(label + "-db"), "s.db"));

    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- PERF: Phan tich folder lon (hash theo nhu cau, cache, huy) ---");
        NoHashWhenSizeAndMtimeMatch();
        HashOnlyAmbiguous();
        TwoWaySameSizeEdits();
        PersistentCache();
        CacheClearedWhenRootsChange();
        RenameWithoutStateHash();
        RenameDetectorLinear();
        ScanProgressAndCancel();
        return (_pass, _fail);
    }

    // --- 300 file giong het 2 phia (cung size + mtime): KHONG doc noi dung file nao ---
    private static void NoHashWhenSizeAndMtimeMatch()
    {
        var left = NewTempDir("nh-l"); var right = NewTempDir("nh-r");
        for (int i = 0; i < 300; i++)
        {
            Write(left, $"d{i % 10}/f{i}.txt", "noi dung " + i, T0.AddMinutes(i));
            Write(right, $"d{i % 10}/f{i}.txt", "noi dung " + i, T0.AddMinutes(i));
        }
        using var store = NewStore("nh");
        var o = new SyncSession("j", left, right, SyncMode.MirrorOneWay, store).AnalyzeOnly();
        Check("300 file cung size + mtime: khong hash file nao, khong hanh dong nao",
            o.HashedFiles == 0 && o.Actions.All(a => a.Kind == SyncActionKind.Skip), $"hashed={o.HashedFiles}");
    }

    // --- Cung size khac mtime: CHI cac file do bi hash; noi dung giong -> Skip, khac -> chep ---
    private static void HashOnlyAmbiguous()
    {
        var left = NewTempDir("ha-l"); var right = NewTempDir("ha-r");
        for (int i = 0; i < 50; i++) { Write(left, $"same{i}.txt", "x" + i, T0); Write(right, $"same{i}.txt", "x" + i, T0); }
        Write(left, "touch.txt", "giong nhau", T0); Write(right, "touch.txt", "giong nhau", T0.AddHours(3)); // chi bi "touch"
        Write(left, "sua.txt", "AAAA", T0.AddHours(5)); Write(right, "sua.txt", "BBBB", T0);                 // cung size, khac noi dung
        Write(left, "dai.txt", "ngan", T0); Write(right, "dai.txt", "dai hon nhieu", T0);                     // khac size
        using var store = NewStore("ha");
        var o = new SyncSession("j", left, right, SyncMode.MirrorOneWay, store).AnalyzeOnly();
        var k = o.Actions.ToDictionary(a => a.RelativePath, a => a.Kind);
        Check("Chi hash 4 file mo ho (touch.txt, sua.txt x 2 phia) - khong hash 50 file giong, khong hash file khac size",
            o.HashedFiles == 4, $"hashed={o.HashedFiles}");
        Check("Cung size: chi khac mtime -> Skip; khac noi dung -> chep; khac size -> chep",
            k["touch.txt"] == SyncActionKind.Skip && k["sua.txt"] == SyncActionKind.CopyLeftToRight
            && k["dai.txt"] == SyncActionKind.CopyLeftToRight, string.Join(",", k.Where(kv => kv.Value != SyncActionKind.Skip)));
    }

    // --- 2 chieu: state co hash (tu luc chep) -> sua cung size phat hien dung, chi "touch" thi khong chep ---
    private static void TwoWaySameSizeEdits()
    {
        var left = NewTempDir("tw-l"); var right = NewTempDir("tw-r");
        Write(left, "bao-cao.txt", "phien ban 1", T0);
        Write(left, "ghi-chu.txt", "ghi chu A", T0);
        using var store = NewStore("tw");
        var s = new SyncSession("j", left, right, SyncMode.TwoWay, store);
        s.Run();
        Check("State sau lan chep dau co hash (lay tu luc chep, khong doc lai)",
            store.LoadState("j").Values.All(e => e.Hash is not null), string.Join(",", store.LoadState("j").Values.Select(e => e.Hash ?? "null")));

        Write(left, "bao-cao.txt", "phien ban 2", T0.AddMinutes(10)); // cung do dai, noi dung khac
        File.SetLastWriteTimeUtc(Full(left, "ghi-chu.txt"), T0.AddMinutes(10)); // chi doi mtime
        var k = s.AnalyzeOnly().Actions.ToDictionary(a => a.RelativePath, a => a.Kind);
        Check("2 chieu: sua cung size -> chep sang Phai; chi doi mtime -> Skip (khong chep thua)",
            k["bao-cao.txt"] == SyncActionKind.CopyLeftToRight && k["ghi-chu.txt"] == SyncActionKind.Skip,
            string.Join(",", k));
    }

    // --- Cache hash luu DB: "mo lai app" (ScanCache moi) khong phai hash lai ---
    private static void PersistentCache()
    {
        var left = NewTempDir("pc-l"); var right = NewTempDir("pc-r");
        for (int i = 0; i < 20; i++) { Write(left, $"f{i}.txt", "giong " + i, T0); Write(right, $"f{i}.txt", "giong " + i, T0.AddHours(1)); }
        using var store = NewStore("pc");
        store.UpsertJob("j", "J", left, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins);
        var first = new SyncSession("j", left, right, SyncMode.BackupOneWay, store, scanCache: new ScanCache()).AnalyzeOnly();
        var second = new SyncSession("j", left, right, SyncMode.BackupOneWay, store, scanCache: new ScanCache()).AnalyzeOnly();
        Check("Cache hash luu DB: lan dau hash 40 file mo ho, 'mo lai app' (cache moi) hash 0 file",
            first.HashedFiles == 40 && second.HashedFiles == 0, $"lan1={first.HashedFiles} lan2={second.HashedFiles}");
    }

    private static void CacheClearedWhenRootsChange()
    {
        var left = NewTempDir("cc-l"); var right = NewTempDir("cc-r"); var other = NewTempDir("cc-o");
        Write(left, "a.txt", "a", T0); Write(right, "a.txt", "a", T0.AddHours(1));
        using var store = NewStore("cc");
        store.UpsertJob("j", "J", left, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins);
        new SyncSession("j", left, right, SyncMode.BackupOneWay, store, scanCache: new ScanCache()).AnalyzeOnly();
        bool had = store.LoadHashCache("j").Left.Count == 1;
        store.UpsertJob("j", "J", other, right, SyncMode.BackupOneWay, ConflictPolicy.NewerWins);
        var (l, r) = store.LoadHashCache("j");
        Check("Doi folder -> cache hash cua cap folder cu bi xoa", had && l.Count == 0 && r.Count == 0);
    }

    // --- Doi ten van nhan dien duoc khi state KHONG co hash (so noi dung file that o dich) ---
    private static void RenameWithoutStateHash()
    {
        var left = NewTempDir("rn-l"); var right = NewTempDir("rn-r");
        Write(left, "moi-ten.txt", "noi dung doi ten", T0);
        Write(right, "ten-cu.txt", "noi dung doi ten", T0);
        for (int i = 0; i < 4; i++) { Write(left, $"dem{i}.txt", "d" + i, T0); Write(right, $"dem{i}.txt", "d" + i, T0); }
        using var store = NewStore("rn");
        var state = Enumerable.Range(0, 4).Select(i => new FileEntry($"dem{i}.txt", 2, T0))
            .Append(new FileEntry("ten-cu.txt", 16, T0)) // KHONG co hash
            .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        store.SaveState("j", state);
        var o = new SyncSession("j", left, right, SyncMode.TwoWay, store).AnalyzeOnly();
        Check("Doi ten khi state khong co hash -> van sinh MoveRight (hash rieng 2 file ung vien)",
            o.Actions.Any(a => a.Kind == SyncActionKind.MoveRight && a.TargetPath == "moi-ten.txt") && o.HashedFiles == 2,
            $"hashed={o.HashedFiles} {string.Join(",", o.Actions.Where(a => a.Kind != SyncActionKind.Skip).Select(a => a.Kind + ":" + a.RelativePath))}");
    }

    // --- 20.000 xoa x 20.000 chep: phai chay tuyen tinh (ban cu O(n^2) ~ phut) ---
    private static void RenameDetectorLinear()
    {
        int n = 20000;
        var state = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var left = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var right = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var actions = new List<SyncAction>();
        for (int i = 0; i < n; i++)
        {
            string a = $"cu/{i}.dat", b = $"moi/{i}.dat", h = $"H{i:X8}";
            state[a] = new FileEntry(a, i, T0, h);
            right[a] = new FileEntry(a, i, T0, h);
            left[b] = new FileEntry(b, i, T0, h);
            actions.Add(new SyncAction(SyncActionKind.DeleteRight, a, ""));
            actions.Add(new SyncAction(SyncActionKind.CopyLeftToRight, b, ""));
        }
        var sw = Stopwatch.StartNew();
        var result = RenameDetector.Apply(actions, state, left, right);
        sw.Stop();
        Check($"RenameDetector {n} xoa x {n} chep: ghep du {n} lenh Move trong < 2 giay",
            result.Count == n && result.All(a => a.Kind == SyncActionKind.MoveRight) && sw.Elapsed.TotalSeconds < 2,
            $"moves={result.Count(a => a.Kind == SyncActionKind.MoveRight)} {sw.Elapsed.TotalSeconds:0.00}s");
    }

    // --- Tien trinh quet + Huy (luc phan tich va giua luc chep) ---
    private static void ScanProgressAndCancel()
    {
        var left = NewTempDir("pg-l"); var right = NewTempDir("pg-r");
        for (int i = 0; i < 200; i++) Write(left, $"t{i % 20}/f{i}.txt", "x" + i);
        File.WriteAllBytes(Full(left, "lon.bin"), new byte[32 * 1024 * 1024]);
        using var store = NewStore("pg");
        var events = new List<SyncProgress>();
        new SyncSession("j", left, right, SyncMode.BackupOneWay, store).AnalyzeOnly(new Collect(events));
        var last = events.LastOrDefault(e => e.Stage == SyncProgressStage.Scanning);
        Check("Phan tich bao tien trinh Scanning (so file, thu muc) - khong dung im",
            last is not null && events.Any(e => e.Stage == SyncProgressStage.Scanning && e.BytesDone == 201),
            string.Join(",", events.Select(e => e.Stage + ":" + e.BytesDone)));

        bool threw = false;
        using (var pre = new CancellationTokenSource())
        {
            pre.Cancel();
            try { new SyncSession("j", left, right, SyncMode.BackupOneWay, store).AnalyzeOnly(null, pre.Token); }
            catch (OperationCanceledException) { threw = true; }
        }
        Check("Huy luc phan tich -> OperationCanceledException, khong ra ket qua do", threw);

        // Huy ngay khi bat dau chep file lon -> dung, xoa file tam, khong luu state
        using var cts = new CancellationTokenSource();
        var r = new SyncSession("j", left, right, SyncMode.BackupOneWay, store)
            .Run(new CancelOnBytes(cts, "lon.bin"), cts.Token).Result!;
        Check("Huy giua luc chep: Canceled, khong sot file tam .tmp-sync, khong luu state",
            r.Canceled && !Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Any()
            && store.LoadState("j").Count == 0,
            $"canceled={r.Canceled} copied={r.Copied} state={store.LoadState("j").Count}");
    }

    private sealed class Collect(List<SyncProgress> sink) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => sink.Add(value);
    }

    private sealed class CancelOnBytes(CancellationTokenSource cts, string file) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value)
        {
            if (value.Stage == SyncProgressStage.Bytes && value.Action?.RelativePath == file) cts.Cancel();
        }
    }
}
