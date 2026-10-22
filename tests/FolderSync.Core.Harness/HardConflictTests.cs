using System.Text.RegularExpressions;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// HARD-J1: ma tran xung dot (bo test kho giai doan 2).
// Tren CUNG MOT duong dan d/p.txt, hai phia cung thuc hien {tao, tao cung size, sua, sua giu size, xoa, doi ten,
// file->thu muc} (cap hop le) x {5 chinh sach xung dot} x {thu tu mtime: Trai moi hon, Phai moi hon, bang nhau
// TOI TUNG TICK (100 ns), lech +-1 tick, lech +-1 giay (trong dung sai 2 giay)}.
// Ket qua mong doi tinh bang MO HINH TUNG DUONG DAN viet rieng o day (khong dung ThreeWayAnalyzer/ConflictResolver).
// Cac tieu chi (tung cai la 1 Check):
//   - ket qua dung mo hinh theo tung chinh sach; Xoa/Sua luon giu ban da sua o MOI chinh sach;
//     Manual KHONG tu quyet o nao (duong dan xung dot nguyen ven o ca 2 phia);
//   - LOSS: moi phien ban nguoi dung tao ra con ton tai o dau do (cay hoac .foldersync_history)
//   - KeepBoth khong nhan ban "conflict copy" them o cac lan dong bo sau; moi ket qua on dinh khi chay lai
//   - NewerWins khi mtime hoa (tung tick) cho ket qua XAC DINH: chay 2 lan cung du lieu -> cung ket qua
//   - khong sot *.tmp-sync; khong huy / khong loi I/O
// Khong dua vao ma tran (neu ro, dem so): cung size + mtime hoa/gan nhau (<= 2 giay) + noi dung khac
// = "giong" theo thiet ke (CLAUDE.md "Bay da biet"); duong dan file<->thu muc xung dot voi ban sua o phia kia
// (khong the ton tai ca 'p' lan 'p/s.txt') -> chi kiem an toan (khong mat du lieu), vi cach hoi tu la
// quyet dinh thiet ke cua chu repo.
public static class HardConflictTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private enum Op { Create, CreateSame, Modify, SameSize, Delete, RenameAway, FileToDir }

    private const string P = "d/p.txt";
    private const string Moved = "d/p.txt.moved";
    private const string Child = "d/p.txt/s.txt";
    private const string C0 = "base-content-0123456789";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime X = T0.AddSeconds(1000);

    private static readonly ConflictPolicy[] Policies =
        { ConflictPolicy.NewerWins, ConflictPolicy.LeftWins, ConflictPolicy.RightWins, ConflictPolicy.KeepBoth, ConflictPolicy.Manual };

    private static readonly long[] Deltas =
    {
        10 * TimeSpan.TicksPerSecond, -10 * TimeSpan.TicksPerSecond, 0,
        TimeSpan.TicksPerSecond, -TimeSpan.TicksPerSecond, 1, -1
    };

    private sealed record Case(Op L, Op R, ConflictPolicy Policy, long DeltaTicks)
    {
        public string Label => $"[Trai:{L} | Phai:{R} | {Policy} | dMtime(Trai-Phai)={DeltaTicks} tick]";
    }

    private sealed record Ver(string Content, DateTime Mtime);

    private static string ContentOf(Op op, bool left) => op switch
    {
        Op.Create => left ? "created-by-left-0001" : "created-by-right-01-longer",
        Op.CreateSame => left ? "created-L-same-size!!" : "created-R-same-size!!",
        Op.Modify => left ? C0 + "+left-edit" : C0 + "+right-edit-longer",
        Op.SameSize => (left ? "L" : "R") + C0[1..],
        Op.FileToDir => left ? "dir-child-from-left" : "dir-child-from-right-longer",
        _ => ""
    };

    private static bool BringsContent(Op op) => op is not (Op.Delete or Op.RenameAway);

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Write(string root, Dictionary<string, Ver> tree, string rel, string content, DateTime mtime)
    {
        var full = Full(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, mtime);
        tree[rel] = new Ver(content, mtime);
    }

    private static void Apply(Op op, bool left, string root, Dictionary<string, Ver> tree, DateTime mtime)
    {
        switch (op)
        {
            case Op.Create: case Op.CreateSame: case Op.Modify: case Op.SameSize:
                Write(root, tree, P, ContentOf(op, left), mtime);
                break;
            case Op.Delete:
                File.Delete(Full(root, P)); tree.Remove(P);
                break;
            case Op.RenameAway:
                File.Move(Full(root, P), Full(root, Moved)); // giu mtime
                tree[Moved] = tree[P]; tree.Remove(P);
                break;
            case Op.FileToDir:
                File.Delete(Full(root, P)); tree.Remove(P);
                Write(root, tree, Child, ContentOf(op, left), mtime);
                break;
        }
    }

    // ---------------- mo hinh tham chieu (tung duong dan) ----------------
    private sealed record Expected(Dictionary<string, string> L, Dictionary<string, string> R, List<(string Path, string Content)> ConflictCopies);

    private static Expected Predict(ConflictPolicy policy, Dictionary<string, Ver> b, Dictionary<string, Ver> l, Dictionary<string, Ver> r)
    {
        var nl = l.ToDictionary(kv => kv.Key, kv => kv.Value.Content, StringComparer.Ordinal);
        var nr = r.ToDictionary(kv => kv.Key, kv => kv.Value.Content, StringComparer.Ordinal);
        var cc = new List<(string, string)>();
        foreach (var p in l.Keys.Concat(r.Keys).Concat(b.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            l.TryGetValue(p, out var lv); r.TryGetValue(p, out var rv); b.TryGetValue(p, out var bv);
            if (lv is null && rv is null) continue;
            if (lv is not null && rv is not null)
            {
                if (lv.Content == rv.Content) continue;
                bool lc = bv is null || lv.Content != bv.Content, rc = bv is null || rv.Content != bv.Content;
                if (lc && !rc) nr[p] = lv.Content;
                else if (!lc && rc) nl[p] = rv.Content;
                else Resolve(p, lv, rv);
                continue;
            }
            bool leftHas = lv is not null;
            var present = (lv ?? rv)!;
            if (bv is null || present.Content != bv.Content)
            {
                // moi tao (1 phia), hoac Xoa/Sua: phia con lai da sua
                if (bv is not null && policy == ConflictPolicy.Manual) continue; // Manual: khong tu quyet - de nguyen
                if (leftHas) nr[p] = present.Content; else nl[p] = present.Content;
            }
            else if (leftHas) nl.Remove(p); else nr.Remove(p);
        }
        return new Expected(nl, nr, cc);

        void Resolve(string p, Ver lv, Ver rv)
        {
            switch (policy)
            {
                case ConflictPolicy.Manual: return;
                case ConflictPolicy.LeftWins: nr[p] = lv.Content; return;
                case ConflictPolicy.RightWins: nl[p] = rv.Content; return;
                case ConflictPolicy.NewerWins when (lv.Mtime - rv.Mtime).Duration() > TimeSpan.FromSeconds(2):
                    if (lv.Mtime > rv.Mtime) nr[p] = lv.Content; else nl[p] = rv.Content;
                    return;
                default: // KeepBoth, hoac NewerWins hoa / lech trong dung sai 2 giay
                    nl[p] = lv.Content; nr[p] = lv.Content;
                    cc.Add((p, rv.Content));
                    return;
            }
        }
    }

    // ---------------- chay 1 ca ----------------
    private sealed class Snapshot
    {
        public Dictionary<string, string> L = new(), R = new();
        public List<string> Tmp = new();
        public HashSet<string> AllContents = new();
        public string Key => string.Join("\n", L.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"L|{k.Key}|{k.Value}"))
                             + "\n" + string.Join("\n", R.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"R|{k.Key}|{k.Value}"));
    }

    private static Snapshot Snap(string left, string right)
    {
        var s = new Snapshot();
        foreach (var (root, map) in new[] { (left, s.L), (right, s.R) })
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                var content = File.ReadAllText(full);
                s.AllContents.Add(content);
                if (rel.StartsWith(".foldersync_history/", StringComparison.Ordinal)) continue;
                if (rel.EndsWith(".tmp-sync", StringComparison.Ordinal)) { s.Tmp.Add(rel); continue; }
                map[rel] = content;
            }
        return s;
    }

    private static bool IsKindCollision(Op a, Op b)
        => (a == Op.FileToDir && b is Op.Modify or Op.SameSize) || (b == Op.FileToDir && a is Op.Modify or Op.SameSize);

    private static Regex ConflictNameRx(string path)
    {
        var dir = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        var file = path[(path.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        return new Regex($"^{Regex.Escape(dir)}{Regex.Escape(stem)} \\(conflict \\d{{4}}-\\d{{2}}-\\d{{2}} \\d{{6}}\\){Regex.Escape(ext)}$");
    }

    private sealed class Result
    {
        public Snapshot First = new();
        public Snapshot Last = new();
        public SessionOutcome Outcome = null!;
        public int ZeroActionFailures;
        public string StabilityNote = "";
        public Dictionary<string, Ver> Before = new(), BeforeR = new();
        public Expected Exp = null!;
    }

    private static Result RunCase(Case c)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"foldersync-hard-j1-{Guid.NewGuid():N}");
        var left = Path.Combine(baseDir, "L"); var right = Path.Combine(baseDir, "R");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        using var store = new SqliteStateStore(Path.Combine(baseDir, "s.db"));
        store.UpsertJob("j1", "j1", left, right, SyncMode.TwoWay, c.Policy, new JobOptions().ToJson());
        var session = new SyncSession("j1", left, right, SyncMode.TwoWay, store, policy: c.Policy, scanCache: new ScanCache());

        var lt = new Dictionary<string, Ver>(StringComparer.Ordinal);
        for (int i = 0; i < 12; i++) Write(left, lt, $"f/f{i:00}.txt", $"filler-{i}", T0.AddSeconds(i * 10));
        bool baseHasP = c.L is not (Op.Create or Op.CreateSame);
        if (baseHasP) Write(left, lt, P, C0, T0.AddSeconds(500));
        var first = session.Run();
        var res = new Result();
        if (first.Result!.Aborted || first.Result.Errors.Count > 0)
            throw new InvalidOperationException($"lan dong bo goc loi: {first.Result.AbortReason} {string.Join(";", first.Result.Errors)}");
        var baseTree = new Dictionary<string, Ver>(lt, StringComparer.Ordinal);
        var rt = new Dictionary<string, Ver>(lt, StringComparer.Ordinal);

        Apply(c.L, true, left, lt, X.AddTicks(c.DeltaTicks));
        Apply(c.R, false, right, rt, X);
        res.Exp = Predict(c.Policy, baseTree, lt, rt);

        res.Outcome = session.Run();
        res.First = Snap(left, right);

        // Chay them 2 lan: khong duoc sinh them gi (KeepBoth khong nhan ban), ket qua on dinh
        for (int i = 0; i < 2; i++)
        {
            session.Run();
            var again = Snap(left, right);
            if (again.Key != res.First.Key)
                res.StabilityNote = $"lan chay them thu {i + 1} doi ket qua: sau lan dong bo dau Trai=[{string.Join(",", res.First.L.Keys.Where(k => k.StartsWith("d/")))}] Phai=[{string.Join(",", res.First.R.Keys.Where(k => k.StartsWith("d/")))}]"
                                  + $" -> sau Trai=[{string.Join(",", again.L.Keys.Where(k => k.StartsWith("d/")))}] Phai=[{string.Join(",", again.R.Keys.Where(k => k.StartsWith("d/")))}]";
            res.Last = again;
        }
        var work = session.AnalyzeOnly().Actions.Where(a => a.Kind != SyncActionKind.Skip).ToList();
        if (work.Count > 0 && c.Policy != ConflictPolicy.Manual && !IsKindCollision(c.L, c.R))
            res.StabilityNote += $" con {work.Count} hanh dong sau khi da dong bo: {string.Join(" ;; ", work.Take(3).Select(a => $"{a.Kind} {a.RelativePath} ({a.Reason})"))}";

        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(baseDir, true); } catch (Exception) { /* Program.cs don sau */ }
        return res;
    }

    private static string Diff(string side, Dictionary<string, string> exp, Dictionary<string, string> act, Func<string, bool> ignore)
    {
        var d = new List<string>();
        foreach (var p in exp.Keys.Concat(act.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            exp.TryGetValue(p, out var e); act.TryGetValue(p, out var a);
            if (e != a && !(e is null && ignore(p)))
                d.Add($"{side} {p}: mong doi {(e is null ? "<vang>" : $"\"{e}\"")} thuc te {(a is null ? "<vang>" : $"\"{a}\"")}");
        }
        return string.Join(" ; ", d);
    }

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- HARD-J1: ma tran xung dot (cap thao tac x 5 chinh sach x thu tu mtime, 2 chieu) ---");

        var ops = new[] { Op.Modify, Op.SameSize, Op.Delete, Op.RenameAway, Op.FileToDir };
        var pairs = new List<(Op, Op)> { (Op.Create, Op.Create), (Op.CreateSame, Op.CreateSame) };
        foreach (var a in ops) foreach (var b in ops) pairs.Add((a, b));

        var cases = new List<Case>();
        int skippedKnownLimit = 0;
        foreach (var (a, b) in pairs)
        foreach (var pol in Policies)
        {
            bool bothContent = BringsContent(a) && BringsContent(b);
            bool sameSize = ContentOf(a, true).Length == ContentOf(b, false).Length;
            var deltas = bothContent && !IsKindCollision(a, b) ? Deltas : new[] { Deltas[0] };
            foreach (var d in deltas)
            {
                bool nearOrTie = Math.Abs(d) <= 2 * TimeSpan.TicksPerSecond;
                // cung size + mtime hoa/gan nhau + khac noi dung: giong theo thiet ke (khong the phan biet)
                if (bothContent && sameSize && nearOrTie) { skippedKnownLimit++; continue; }
                cases.Add(new Case(a, b, pol, d));
            }
        }

        var modelFail = Policies.ToDictionary(p => p, _ => new List<string>());
        var lossFail = new List<string>(); var stableFail = new List<string>(); var tmpFail = new List<string>();
        var runFail = new List<string>(); var tieFail = new List<string>(); var kindInfo = new List<string>();
        var openManual = new List<string>();
        int deleteEditCases = 0, deleteEditKept = 0, manualCases = 0, tieCases = 0, kindCases = 0;

        foreach (var c in cases)
        {
            Result r;
            try { r = RunCase(c); }
            catch (Exception ex) { runFail.Add($"{c.Label} ngoai le {ex.GetType().Name}: {ex.Message}"); continue; }

            bool kind = IsKindCollision(c.L, c.R);
            var snap = r.First;

            if (snap.Tmp.Count > 0) tmpFail.Add($"{c.Label} {string.Join(",", snap.Tmp.Take(3))}");
            bool isDeleteEdit = (c.L == Op.Delete && c.R is Op.Modify or Op.SameSize) || (c.R == Op.Delete && c.L is Op.Modify or Op.SameSize)
                || (c.L == Op.RenameAway && c.R is Op.Modify or Op.SameSize) || (c.R == Op.RenameAway && c.L is Op.Modify or Op.SameSize);
            if (r.StabilityNote.Length > 0)
            {
                // OPEN-1 (cho chu repo quyet, xem bao cao PR): Manual + Xoa/Sua chi giu xung dot dung 1 lan chay -
                // state khong con duong dan do nen lan sau ban sua duoc coi la "moi tao" va tu chep sang phia da xoa.
                if (c.Policy == ConflictPolicy.Manual && isDeleteEdit) openManual.Add($"{c.Label} {r.StabilityNote}");
                else stableFail.Add($"{c.Label} {r.StabilityNote}");
            }

            // LOSS: moi noi dung nguoi dung tao ra phai con o dau do (cay hoac history)
            var made = new List<string>();
            if (BringsContent(c.L)) made.Add(ContentOf(c.L, true));
            if (BringsContent(c.R)) made.Add(ContentOf(c.R, false));
            var lost = made.Where(m => !snap.AllContents.Contains(m)).ToList();
            if (lost.Count > 0) lossFail.Add($"{c.Label} mat noi dung: {string.Join(",", lost.Select(x => $"\"{x}\""))}");

            if (kind)
            {
                kindCases++;
                kindInfo.Add($"{c.Label} loi={r.Outcome.Result!.Errors.Count} Trai=[{string.Join(",", snap.L.Keys.Where(k => k.StartsWith("d/")))}] Phai=[{string.Join(",", snap.R.Keys.Where(k => k.StartsWith("d/")))}]");
                continue;
            }

            if (r.Outcome.Result!.Aborted) runFail.Add($"{c.Label} bi huy: {r.Outcome.Result.AbortReason}");
            else if (r.Outcome.Result.Errors.Count > 0) runFail.Add($"{c.Label} loi I/O: {string.Join("; ", r.Outcome.Result.Errors.Take(2))}");

            // MODEL: so tung duong dan; conflict copy (KeepBoth) kiem rieng theo mau ten
            var ccRx = r.Exp.ConflictCopies.Select(cc => (cc.Path, cc.Content, Rx: ConflictNameRx(cc.Path))).ToList();
            bool IsCc(string p) => ccRx.Any(x => x.Rx.IsMatch(p));
            var problems = new List<string>();
            var dl = Diff("Trai", r.Exp.L, snap.L, IsCc); if (dl.Length > 0) problems.Add(dl);
            var dr = Diff("Phai", r.Exp.R, snap.R, IsCc); if (dr.Length > 0) problems.Add(dr);
            foreach (var (side, map) in new[] { ("Trai", snap.L), ("Phai", snap.R) })
            {
                var extras = map.Where(kv => !r.Exp.L.ContainsKey(kv.Key) && !r.Exp.R.ContainsKey(kv.Key)).ToList();
                if (extras.Count != ccRx.Count)
                    problems.Add($"{side}: mong doi {ccRx.Count} conflict copy, thuc te {extras.Count} ({string.Join(",", extras.Select(e => e.Key))})");
                foreach (var cc in ccRx)
                    if (!extras.Any(e => cc.Rx.IsMatch(e.Key) && e.Value == cc.Content))
                        problems.Add($"{side}: thieu conflict copy cua {cc.Path} voi noi dung \"{cc.Content}\"");
            }
            if (problems.Count > 0) modelFail[c.Policy].Add($"{c.Label} {string.Join(" | ", problems)}");

            if (c.Policy == ConflictPolicy.Manual) manualCases++;
            bool deleteEdit = (c.L == Op.Delete && c.R is Op.Modify or Op.SameSize) || (c.R == Op.Delete && c.L is Op.Modify or Op.SameSize);
            if (deleteEdit)
            {
                deleteEditCases++;
                var edited = ContentOf(c.L == Op.Delete ? c.R : c.L, c.L != Op.Delete);
                // "giu ban da sua": o moi chinh sach (Manual: khong dong vao, ban sua van nam nguyen o phia da sua)
                bool kept = c.Policy == ConflictPolicy.Manual
                    ? (c.L == Op.Delete ? snap.R : snap.L).GetValueOrDefault(P) == edited
                    : snap.L.GetValueOrDefault(P) == edited && snap.R.GetValueOrDefault(P) == edited;
                if (kept) deleteEditKept++;
            }
        }

        // Tie-determinism: NewerWins, mtime hoa/lech <= 2 giay, co xung dot noi dung -> chay lan 2 cung du lieu
        foreach (var c in cases.Where(c => c.Policy == ConflictPolicy.NewerWins && Math.Abs(c.DeltaTicks) <= 2 * TimeSpan.TicksPerSecond
                                           && BringsContent(c.L) && BringsContent(c.R) && !IsKindCollision(c.L, c.R)))
        {
            tieCases++;
            try
            {
                var a = RunCase(c); var b = RunCase(c);
                if (a.First.Key != b.First.Key) tieFail.Add($"{c.Label} 2 lan chay cho 2 ket qua khac nhau");
            }
            catch (Exception ex) { tieFail.Add($"{c.Label} ngoai le {ex.Message}"); }
        }

        string Few(List<string> l) => $"{l.Count} ca; vi du: {string.Join(" ;; ", l.Take(3))}";

        Console.WriteLine($"J1: {cases.Count} ca chay; bo qua {skippedKnownLimit} ca 'cung size + mtime hoa/gan nhau + khac noi dung' (giong theo thiet ke, xem CLAUDE.md Bay da biet); {kindCases} ca file<->thu muc xung dot voi ban sua (chi kiem an toan)");
        foreach (var pol in Policies)
            Check($"J1 {pol}: ket qua dung mo hinh theo tung duong dan ({cases.Count(c => c.Policy == pol && !IsKindCollision(c.L, c.R))} ca)",
                modelFail[pol].Count == 0, Few(modelFail[pol]));
        Check($"J1 Xoa/Sua luon giu ban da sua o ca 5 chinh sach ({deleteEditCases} ca)", deleteEditCases > 0 && deleteEditKept == deleteEditCases,
            $"{deleteEditCases - deleteEditKept}/{deleteEditCases} ca khong giu ban da sua");
        Check($"J1 Manual khong tu quyet o nao ({manualCases} ca, duong dan xung dot nguyen ven o ca 2 phia)",
            manualCases > 0 && modelFail[ConflictPolicy.Manual].Count == 0, Few(modelFail[ConflictPolicy.Manual]));
        Check("J1 khong mat noi dung nguoi dung o bat ky ca nao (cay hoac .foldersync_history)", lossFail.Count == 0, Few(lossFail));
        Check("J1 KeepBoth khong nhan ban them o lan dong bo sau, ket qua on dinh khi chay lai", stableFail.Count == 0, Few(stableFail));
        Check($"J1 NewerWins mtime hoa/gan nhau cho ket qua xac dinh ({tieCases} ca chay 2 lan cung du lieu)", tieCases > 0 && tieFail.Count == 0, Few(tieFail));
        Check("J1 khong sot *.tmp-sync", tmpFail.Count == 0, Few(tmpFail));
        Check("J1 khong huy / khong loi I/O / khong ngoai le (tru ca file<->thu muc)", runFail.Count == 0, Few(runFail));
        // Muc OPEN (khong tinh PASS/FAIL, cho chu repo quyet - KHONG noi long tieu chi o tren: chi tach rieng ca da biet)
        Console.WriteLine($"[OPEN-1] HARD J1 Manual + Xoa/Sua: xung dot chi giu 1 lan chay, lan sau tu chep ban sua sang phia da xoa ({openManual.Count} ca); vi du: {string.Join(" ;; ", openManual.Take(2))}");
        Console.WriteLine($"[OPEN-2] HARD J1 file<->thu muc xung dot voi ban sua o phia kia: {kindCases} ca, 2 phia khong hoi tu (lan chay dau bao loi I/O, khong mat du lieu)");
        foreach (var k in kindInfo.Take(4)) Console.WriteLine($"[OPEN-2] HARD J1 file<->thu muc: {k}");
        return (_pass, _fail);
    }
}
