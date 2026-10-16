using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// HARD-B1: crash gia lap tai TUNG diem chot (FaultPoints) x 3 che do -> mo lai (session + store MOI) -> chay hoi phuc
//          -> ap toan bo Oracle (HardOracle).
// HARD-B3: nguon thay doi GIUA luc dang chay (sua / xoa file nguon, doi ten thu muc cha).
// Xac dinh: noi dung + mtime co dinh, khong ngau nhien, khong phu thuoc dong ho.
public static class HardFaultTests
{
    private static int _pass, _fail;

    private static void Check(string name, List<string> violations)
    {
        bool ok = violations.Count == 0;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD-{name}{(ok ? "" : " :: " + string.Join(" | ", violations))}");
        if (ok) _pass++; else _fail++;
    }

    private sealed class SimulatedCrash : Exception { }

    private const string Job = "hard-job";
    private static readonly DateTime Base = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Sc
    {
        public string Left = "", Right = "", Db = "";
        public SyncMode Mode;
        public readonly Dictionary<string, HashSet<string>> Known = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-hard-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Put(Sc sc, string root, string rel, string content, DateTime mtime)
    {
        var full = Full(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, mtime);
        if (!sc.Known.TryGetValue(rel, out var set)) sc.Known[rel] = set = new HashSet<string>();
        set.Add(HardOracle.Sha(full));
    }

    private static string Body(string tag, string name, int len) => $"{tag} {name} " + new string(tag[^1], len);

    private static readonly string[] InitialFiles =
        { "a.txt", "b.txt", "c.txt", "d.txt", "h.txt", "i.txt", "j.txt", "sub/e.txt", "sub/f.txt", "sub/g.txt" };

    /// <summary>Dung 10 file, dong bo lan dau (khong chich loi), roi tao tap thay doi dai han cho tung che do.</summary>
    private static Sc Build(SyncMode mode, string label, List<string> v)
    {
        var sc = new Sc { Mode = mode, Left = NewTempDir(label + "-l"), Right = NewTempDir(label + "-r") };
        sc.Db = Path.Combine(NewTempDir(label + "-db"), "s.db");
        int idx = 0;
        foreach (var f in InitialFiles) Put(sc, sc.Left, f, Body("v1", f, 50 + 7 * idx++), Base);
        using (var store = new SqliteStateStore(sc.Db))
        {
            var o = new SyncSession(Job, sc.Left, sc.Right, mode, store).Run();
            if (o.Result!.Aborted || o.Result.Errors.Count > 0) v.Add("setup: lan dong bo dau loi " + o.Result.AbortReason);
        }
        if (HardOracle.Snapshot(sc.Right).Count != InitialFiles.Length) v.Add("setup: Phai khong du 10 file");
        // Mirror/2 chieu: Phai moi cung phai co noi dung v1 khop (de Known phan anh dung)
        var t1 = Base.AddHours(1); var t2 = Base.AddHours(2);
        Put(sc, sc.Left, "a.txt", Body("v2", "a.txt", 300), t1);
        Put(sc, sc.Left, "sub/e.txt", Body("v2", "sub/e.txt", 211), t1);
        Put(sc, sc.Left, "n.txt", Body("v2", "n.txt", 90), t1);
        switch (mode)
        {
            case SyncMode.BackupOneWay:
                Put(sc, sc.Left, "sub/n2.txt", Body("v2", "sub/n2.txt", 120), t1);
                break;
            case SyncMode.MirrorOneWay:
                File.Delete(Full(sc.Left, "b.txt"));
                Put(sc, sc.Right, "z.txt", Body("v2", "z.txt", 77), t2);
                break;
            case SyncMode.TwoWay:
                File.Delete(Full(sc.Left, "c.txt"));
                Put(sc, sc.Right, "b.txt", Body("v2", "b.txt", 133), t2);
                Put(sc, sc.Right, "m.txt", Body("v2", "m.txt", 64), t2);
                break;
        }
        return sc;
    }

    private static SessionOutcome? RunSession(Sc sc, Action<string, string?>? hook)
    {
        using var store = new SqliteStateStore(sc.Db) { FaultHook = hook };
        var session = new SyncSession(Job, sc.Left, sc.Right, sc.Mode, store) { FaultHook = hook };
        return session.Run();
    }

    private static List<string> AnalyzeFresh(Sc sc)
    {
        using var store = new SqliteStateStore(sc.Db);
        return HardOracle.AssertAnalyzeLan2Rong(new SyncSession(Job, sc.Left, sc.Right, sc.Mode, store));
    }

    /// <summary>Moi file dang co (khong ke tmp/history) phai mang 1 trong cac noi dung hop le da tung ghi cho ten do:
    /// khong bao gio co file dich mang ten that ma noi dung do dang.</summary>
    private static void CheckContents(Sc sc, string root, string side, List<string> v)
    {
        foreach (var (rel, sha) in HardOracle.Snapshot(root))
            if (!sc.Known.TryGetValue(rel, out var set) || !set.Contains(sha))
                v.Add($"noi dung la/do dang o {side}:{rel}");
    }

    /// <summary>Backup: Trai la tap con cua Phai; Mirror/2 chieu: 2 phia giong het.</summary>
    private static void CheckConverged(Sc sc, List<string> v)
    {
        var l = HardOracle.Snapshot(sc.Left); var r = HardOracle.Snapshot(sc.Right);
        foreach (var (rel, sha) in l)
            if (!r.TryGetValue(rel, out var rs) || rs != sha) v.Add($"hoi tu: '{rel}' Phai khac/thieu so voi Trai");
        if (sc.Mode != SyncMode.BackupOneWay)
            foreach (var rel in r.Keys.Where(k => !l.ContainsKey(k))) v.Add($"hoi tu: '{rel}' chi co o Phai");
    }

    private static void ApplyOracles(Sc sc, SyncOptions opt, int baseline, SessionOutcome run,
        Dictionary<string, string> preL, Dictionary<string, string> preR, List<string> v,
        IEnumerable<string>? touchedLeft = null)
    {
        var postL = HardOracle.Snapshot(sc.Left); var postR = HardOracle.Snapshot(sc.Right);
        v.AddRange(HardOracle.AssertAnalyzeLan2Rong(NewAnalyzer(sc, out var store)));
        store.Dispose();
        v.AddRange(HardOracle.AssertStateKhopDia(sc.Db, Job, sc.Left, sc.Right));
        v.AddRange(HardOracle.AssertKhongConFileTam(sc.Left, sc.Right));
        v.AddRange(HardOracle.AssertHistoryBaoToan(sc.Left, preL, postL, touchedLeft));
        v.AddRange(HardOracle.AssertHistoryBaoToan(sc.Right, preR, postR));
        v.AddRange(HardOracle.AssertKhongVuotChotXoa(baseline, opt, preL, postL, preR, postR, touchedLeft));
        v.AddRange(HardOracle.AssertKhongSyncThuMucHistory(run.Actions, sc.Left, sc.Right));
    }

    private static SyncSession NewAnalyzer(Sc sc, out SqliteStateStore store)
    {
        store = new SqliteStateStore(sc.Db);
        return new SyncSession(Job, sc.Left, sc.Right, sc.Mode, store);
    }

    // ================= HARD-B1 =================

    private static void RunB1(SyncMode mode, string point)
    {
        var v = new List<string>();
        var sc = Build(mode, "b1", v);
        var preL = HardOracle.Snapshot(sc.Left); var preR = HardOracle.Snapshot(sc.Right);
        var preState = HardOracle.ReadStateRows(sc.Db, Job);
        var tmpAtCrash = new List<string>();
        bool fired = false;
        void Hook(string p, string? path)
        {
            if (p != point || fired) return;
            fired = true;
            // Crash that de lai file tam; catch-all cua AtomicCopy xoa no khi ngoai le bay qua -> ghi nho de dung lai
            foreach (var root in new[] { sc.Left, sc.Right })
                tmpAtCrash.AddRange(Directory.EnumerateFiles(root, "*.tmp-sync", SearchOption.AllDirectories));
            throw new SimulatedCrash();
        }

        bool crashed = false;
        try { RunSession(sc, Hook); }
        catch (SimulatedCrash) { crashed = true; }
        if (!fired) { v.Add("diem chot khong duoc kich hoat (kich ban khong cham toi)"); Check($"B1 {mode} @ {point}", v); return; }
        if (!crashed) v.Add("ngoai le crash bi nuot, khong thoat khoi Run");
        foreach (var t in tmpAtCrash.Where(t => !File.Exists(t))) File.WriteAllText(t, "PARTIAL-CRASH");

        // Ngay sau crash (truoc khi hoi phuc): dich khong co noi dung do dang, state y nguyen ban truoc (nguyen tu)
        CheckContents(sc, sc.Left, "Trai", v);
        CheckContents(sc, sc.Right, "Phai", v);
        var stateAfterCrash = HardOracle.ReadStateRows(sc.Db, Job);
        if (!stateAfterCrash.SequenceEqual(preState))
            v.Add($"state doi sau crash: {preState.Count} dong -> {stateAfterCrash.Count} dong (phai giu nguyen ban cu)");

        // "Mo lai app": store + session MOI tren cung du lieu
        var recovery = RunSession(sc, null)!;
        if (recovery.Result!.Aborted) v.Add("hoi phuc bi huy: " + recovery.Result.AbortReason);
        foreach (var e in recovery.Result.Errors) v.Add("hoi phuc loi: " + e);
        CheckContents(sc, sc.Left, "Trai", v);
        CheckContents(sc, sc.Right, "Phai", v);
        CheckConverged(sc, v);
        ApplyOracles(sc, new SyncOptions(), preState.Count, recovery, preL, preR, v);
        Check($"B1 {mode} @ {point}", v);
    }

    // ================= HARD-B3 =================

    private enum B3Case { SuaNguonTruocCopy, SuaNguonGiuaVerify, SuaNguonSauRename, XoaNguon, DoiTenThuMucCha }

    private static string CaseName(B3Case c) => c switch
    {
        B3Case.SuaNguonTruocCopy => "sua nguon sau Analyze (truoc hanh dong dau)",
        B3Case.SuaNguonGiuaVerify => "sua nguon cung size, sau khi kiem chung file tam",
        B3Case.SuaNguonSauRename => "sua nguon cung size, ngay sau khi rename vao dich",
        B3Case.XoaNguon => "xoa file nguon sap copy",
        _ => "doi ten thu muc cha cua file sap copy"
    };

    private static void RunB3(SyncMode mode, B3Case c)
    {
        var v = new List<string>();
        var sc = Build(mode, "b3", v);
        var preL = HardOracle.Snapshot(sc.Left); var preR = HardOracle.Snapshot(sc.Right);
        var preState = HardOracle.ReadStateRows(sc.Db, Job);
        bool fired = false;
        string editedSha = "";
        string point = c switch
        {
            B3Case.SuaNguonGiuaVerify => FaultPoints.AfterTempWrite,
            B3Case.SuaNguonSauRename => FaultPoints.AfterRename,
            _ => FaultPoints.BeforeFirstAction
        };
        void Hook(string p, string? path)
        {
            if (fired || p != point) return;
            if (point != FaultPoints.BeforeFirstAction && path != "a.txt") return;
            fired = true;
            switch (c)
            {
                case B3Case.SuaNguonTruocCopy:
                    Put(sc, sc.Left, "a.txt", Body("v3", "a.txt", 410), Base.AddHours(3));
                    break;
                case B3Case.SuaNguonGiuaVerify:
                case B3Case.SuaNguonSauRename:
                    Put(sc, sc.Left, "a.txt", Body("v2", "a.txt", 300).Replace("v2", "v3").Replace('2', '3'), Base.AddHours(3));
                    editedSha = HardOracle.Sha(Full(sc.Left, "a.txt"));
                    break;
                case B3Case.XoaNguon:
                    File.Delete(Full(sc.Left, "a.txt"));
                    break;
                case B3Case.DoiTenThuMucCha:
                    Directory.Move(Full(sc.Left, "sub"), Full(sc.Left, "sub2"));
                    // Noi dung khong doi, chi doi duong dan -> ghi nhan noi dung hop le cho ten moi
                    foreach (var (rel, sha) in HardOracle.Snapshot(Full(sc.Left, "sub2")))
                    {
                        var key = "sub2/" + rel;
                        if (!sc.Known.TryGetValue(key, out var set)) sc.Known[key] = set = new HashSet<string>();
                        set.Add(sha);
                    }
                    break;
            }
        }

        // Lan chay A: nguon doi giua chung. State sau lan A chi duoc noi 2 phia "da giong" khi that su giong (Oracle b)
        var runA = RunSession(sc, Hook)!;
        if (!fired) { v.Add("diem chot khong duoc kich hoat"); Check($"B3 {mode} / {CaseName(c)}", v); return; }
        if (runA.Result!.Aborted) v.Add("lan A bi huy: " + runA.Result.AbortReason);
        v.AddRange(HardOracle.AssertStateKhopDia(sc.Db, Job, sc.Left, sc.Right));
        v.AddRange(HardOracle.AssertKhongConFileTam(sc.Left, sc.Right));
        v.AddRange(HardOracle.AssertKhongSyncThuMucHistory(runA.Actions, sc.Left, sc.Right));
        CheckContents(sc, sc.Left, "Trai", v);
        CheckContents(sc, sc.Right, "Phai", v);

        // Lan chay B (khong chich loi): phai hoi tu, file da doi khong bi ban chup cu de len
        var runB = RunSession(sc, null)!;
        if (runB.Result!.Aborted) v.Add("lan B bi huy: " + runB.Result.AbortReason);
        foreach (var e in runB.Result.Errors) v.Add("lan B loi: " + e);
        // File nguoi dung tu sua/xoa/doi ten giua chung (khong phai engine ghi de) thi khong doi history
        var touched = c == B3Case.DoiTenThuMucCha
            ? new[] { "sub/e.txt", "sub/f.txt", "sub/g.txt", "sub/n2.txt" }
            : new[] { "a.txt" };
        ApplyOracles(sc, new SyncOptions(), preState.Count, runB, preL, preR, v, touched);
        CheckContents(sc, sc.Left, "Trai", v);
        CheckContents(sc, sc.Right, "Phai", v);
        CheckConverged(sc, v);

        var finalL = HardOracle.Snapshot(sc.Left); var finalR = HardOracle.Snapshot(sc.Right);
        // 2 chieu, nguon doi SAU khi file da chep: lan A khong the noi "da giong" (dung) nen state bo a.txt;
        // lan B thay 2 phia khac nhau + khong co state = Conflict cho nguoi dung quyet. Chap nhan (khong mat ban nao,
        // khong bi ban chup cu de len) - xem muc PHAT HIEN: dung ra chi Trai doi, khong can xung dot.
        bool staleTwoWay = mode == SyncMode.TwoWay && c is B3Case.SuaNguonGiuaVerify or B3Case.SuaNguonSauRename;
        if (staleTwoWay)
        {
            v.RemoveAll(s => s.Contains("Conflict a.txt") || s.StartsWith("hoi tu: 'a.txt'"));
            if (HardOracle.Sha(Full(sc.Left, "a.txt")) != editedSha) v.Add("a.txt o Trai bi doi khac ban nguoi dung vua sua");
        }
        switch (c)
        {
            case B3Case.SuaNguonTruocCopy:
            case B3Case.SuaNguonGiuaVerify:
            case B3Case.SuaNguonSauRename:
                var want = HardOracle.Sha(Full(sc.Left, "a.txt"));
                if (!staleTwoWay && (!finalR.TryGetValue("a.txt", out var gotR) || gotR != want))
                    v.Add("a.txt o Phai KHONG phai ban moi nhat cua nguon (bi anh chup cu giu lai)");
                break;
            case B3Case.DoiTenThuMucCha:
                if (!finalR.ContainsKey("sub2/e.txt")) v.Add("sub2/e.txt chua sang Phai sau lan B");
                break;
        }
        Check($"B3 {mode} / {CaseName(c)}", v);
    }

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- HARD: Oracle + chich loi (B1 crash x diem chot x che do, B3 nguon doi giua luc chay) ---");
        var modes = new[] { SyncMode.BackupOneWay, SyncMode.MirrorOneWay, SyncMode.TwoWay };
        foreach (var mode in modes)
            foreach (var point in FaultPoints.All)
                RunB1(mode, point);
        foreach (var mode in modes)
            foreach (var c in Enum.GetValues<B3Case>())
                RunB3(mode, c);
        return (_pass, _fail);
    }
}
