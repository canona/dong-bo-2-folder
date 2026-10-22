using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// HARD-A2: doi ten mo ho (bo test kho giai doan 2).
// Cay: 100 file RONG (cung noi dung "") + 100 file noi dung GIONG HET + 50 file trong thu muc "big"
// (tron rong / giong het / rieng) + vai file rieng u/A, u/B... Tren do: doi ten hang loat file trung noi dung,
// HOAN DOI ten A<->B, doi ten thu muc cha chua 50 file con, o 1 phia hoac ca 2 phia (tach roi + xung dot Xoa/Sua).
// PASS = noi dung cuoi cung DUNG THEO TUNG DUONG DAN (so voi cay mong doi tinh bang code rieng, khong
// qua engine), dong bo lan 2 = 0 hanh dong, khong sot *.tmp-sync, khong huy/khong loi. Ghep Move sai
// -> sai noi dung -> FAIL; tha Copy + Delete an toan con hon Move nham (khong bat buoc phai co Move).
public static class HardRenameTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private const string Same = "NOI-DUNG-GIONG-HET-0123456789";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Thao tac len 1 phia: ghi dia (neu co root) va cay mong doi trong bo nho. File.Move/Directory.Move giu mtime.</summary>
    private sealed class Side
    {
        public string? Root;
        public Dictionary<string, string> Tree = new(StringComparer.Ordinal);
        private static int _clock;

        public void Write(string rel, string content, DateTime mtime)
        {
            Tree[rel] = content;
            if (Root is null) return;
            var full = Full(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            File.SetLastWriteTimeUtc(full, mtime);
        }

        public void Edit(string rel, string content) => Write(rel, content, T0.AddDays(60).AddSeconds(++_clock * 10));

        public void Rename(string from, string to)
        {
            Tree[to] = Tree[from];
            Tree.Remove(from);
            if (Root is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Full(Root, to))!);
            File.Move(Full(Root, from), Full(Root, to));
        }

        public void RenameDir(string from, string to)
        {
            foreach (var k in Tree.Keys.Where(k => k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            {
                Tree[to + k[from.Length..]] = Tree[k];
                Tree.Remove(k);
            }
            if (Root is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Full(Root, to))!);
            Directory.Move(Full(Root, from), Full(Root, to));
        }

        /// <summary>Hoan doi ten A<->B qua ten tam, dung nhu nguoi dung that lam.</summary>
        public void Swap(string a, string b)
        {
            var tmp = a + ".swap";
            Rename(a, tmp); Rename(b, a); Rename(tmp, b);
        }
    }

    private static void BuildBase(Side s)
    {
        int k = 0;
        for (int i = 0; i < 100; i++) s.Write($"e/e{i:000}.txt", "", T0.AddSeconds(++k * 10));
        for (int i = 0; i < 100; i++) s.Write($"s/s{i:000}.txt", Same, T0.AddSeconds(++k * 10));
        for (int i = 0; i < 50; i++)
            s.Write($"big/f{i:00}.txt", (i % 3) switch { 0 => "", 1 => Same, _ => $"rieng-big-{i}-" + new string('b', i) }, T0.AddSeconds(++k * 10));
        foreach (var n in "ABCDEFGH")
            s.Write($"u/{n}.txt", $"noi-dung-rieng-{n}-" + new string(n, 10 + n), T0.AddSeconds(++k * 10));
    }

    // Bo thao tac Trai (hang loat + hoan doi + doi ten thu muc cha)
    private static void LeftOps(Side s)
    {
        for (int i = 0; i < 20; i++) s.Rename($"e/e{i:000}.txt", $"e/r/e{i:000}x.txt");           // 20 file rong -> thu muc moi
        for (int i = 0; i < 20; i++) s.Rename($"s/s{i:000}.txt", $"s2/moved{i:000}.txt");         // 20 file giong het
        s.Swap("u/A.txt", "u/B.txt");                                                              // hoan doi ten A<->B (noi dung khac nhau)
        s.RenameDir("big", "big2");                                                                // thu muc cha 50 file con
        s.Rename("u/C.txt", "u/C2.txt");
    }

    // Bo thao tac cua phia kia, TACH ROI voi LeftOps (khong cham cung duong dan)
    private static void OtherOps(Side s)
    {
        s.Rename("u/D.txt", "u/D2.txt");
        s.Rename("e/e050.txt", "e/e050-doi-ten.txt");
        s.Rename("s/s099.txt", "s/s099-doi-ten.txt");
        s.Swap("u/E.txt", "u/F.txt");
        s.Edit("s/s090.txt", "SUA-O-PHIA-KIA");
    }

    private static string NewTemp(string label)
    {
        var d = Path.Combine(Path.GetTempPath(), $"foldersync-hard-a2-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static Dictionary<string, string> ReadTree(string root, List<string> tmp)
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.StartsWith(".foldersync_history/", StringComparison.Ordinal)) continue;
            if (rel.EndsWith(".tmp-sync", StringComparison.Ordinal)) { tmp.Add(rel); continue; }
            m[rel] = File.ReadAllText(full);
        }
        return m;
    }

    private static string Diff(string side, Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        var d = new List<string>();
        foreach (var p in expected.Keys.Concat(actual.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            expected.TryGetValue(p, out var e); actual.TryGetValue(p, out var a);
            if (e != a)
                d.Add($"{side} {p}: mong doi {(e is null ? "<vang>" : $"\"{Cut(e)}\"")} thuc te {(a is null ? "<vang>" : $"\"{Cut(a)}\"")}");
        }
        return d.Count == 0 ? "" : $"{d.Count} duong dan sai, vi du: {string.Join(" ;; ", d.Take(4))}";
    }

    private static string Cut(string s) => s.Length > 16 ? s[..16] + ".." : s;

    /// <param name="applyLeft">Thao tac Trai.</param>
    /// <param name="applyRight">Thao tac Phai (null = khong co).</param>
    /// <param name="expected">Cay mong doi cuoi cung, tinh tu cay goc bang code rieng.</param>
    private static void Scenario(string name, SyncMode mode, ConflictPolicy policy,
        Action<Side>? applyLeft, Action<Side>? applyRight, Func<Dictionary<string, string>> expected,
        Func<Dictionary<string, string>>? expectedRight = null)
    {
        var baseDir = NewTemp(name.Split(' ')[0]);
        var left = Path.Combine(baseDir, "L"); var right = Path.Combine(baseDir, "R");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        using var store = new SqliteStateStore(Path.Combine(baseDir, "s.db"));
        store.UpsertJob("a2", name, left, right, mode, policy, new JobOptions().ToJson());
        var session = new SyncSession("a2", left, right, mode, store, policy: policy, scanCache: new ScanCache());

        var ls = new Side { Root = left };
        BuildBase(ls);
        var first = session.Run();
        var baseTree = new Dictionary<string, string>(ls.Tree);

        applyLeft?.Invoke(ls);
        if (applyRight is not null)
        {
            var rs = new Side { Root = right, Tree = new Dictionary<string, string>(baseTree, StringComparer.Ordinal) };
            applyRight(rs);
        }
        var o = session.Run();

        var tmp = new List<string>();
        var dl = ReadTree(left, tmp); var dr = ReadTree(right, tmp);
        var exp = expected();
        var problems = new List<string>();
        var dL = Diff("Trai", exp, dl); if (dL.Length > 0) problems.Add(dL);
        var dR = Diff("Phai", expectedRight?.Invoke() ?? exp, dr); if (dR.Length > 0) problems.Add(dR);
        if (tmp.Count > 0) problems.Add($"sot file tam: {string.Join(", ", tmp.Take(3))}");
        if (first.Result!.Aborted || first.Result.Errors.Count > 0)
            problems.Add($"lan dong bo dau loi: {first.Result.AbortReason} {string.Join("; ", first.Result.Errors.Take(2))}");
        if (o.Result!.Aborted) problems.Add($"bi huy: {o.Result.AbortReason}");
        if (o.Result.Errors.Count > 0) problems.Add($"loi I/O: {string.Join("; ", o.Result.Errors.Take(3))}");
        var again = session.AnalyzeOnly().Actions.Where(a => a.Kind != SyncActionKind.Skip).ToList();
        if (again.Count > 0)
            problems.Add($"dong bo lan 2 con {again.Count} hanh dong: {string.Join(" ;; ", again.Take(3).Select(a => $"{a.Kind} {a.RelativePath} ({a.Reason})"))}");

        Check($"A2 {name} (Move={o.Result.Moved} Copy={o.Result.Copied} Delete={o.Result.Deleted})",
            problems.Count == 0, string.Join(" | ", problems));
    }

    private static Dictionary<string, string> Expect(params Action<Side>[] ops)
    {
        var s = new Side();
        BuildBase(s);
        foreach (var op in ops) op(s);
        return s.Tree;
    }

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- HARD-A2: doi ten mo ho (100 file rong + 100 giong het + hoan doi A<->B + doi ten thu muc cha 50 file) ---");

        Scenario("1 Mirror: doi ten o Trai", SyncMode.MirrorOneWay, ConflictPolicy.NewerWins,
            LeftOps, null, () => Expect(LeftOps));

        Scenario("2 2 chieu: doi ten o Trai", SyncMode.TwoWay, ConflictPolicy.Manual,
            LeftOps, null, () => Expect(LeftOps));

        // Cung bo thao tac nhung o phia Phai: phia Trai phai theo
        Scenario("3 2 chieu: doi ten o Phai", SyncMode.TwoWay, ConflictPolicy.Manual,
            null, LeftOps, () => Expect(LeftOps));

        Scenario("4 2 chieu: Trai va Phai doi ten tach roi cung luc", SyncMode.TwoWay, ConflictPolicy.Manual,
            LeftOps, OtherOps, () => Expect(LeftOps, OtherOps));

        Scenario("5 Backup: doi ten o Trai (dich giu ban cu, co ban moi)", SyncMode.BackupOneWay, ConflictPolicy.Manual,
            LeftOps, null, () => Expect(LeftOps), () =>
            {
                // Backup khong xoa o dich: Phai = cay goc + cac duong dan moi cua Trai
                var t = Expect();
                foreach (var (k, v) in Expect(LeftOps)) t[k] = v;
                return t;
            });

        // Xoa/Sua: Trai doi ten ca thu muc big -> big2, Phai sua 1 file trong big -> ban da sua phai song
        // (bat bien 7), big2 van du 50 file voi noi dung CU.
        Scenario("6 2 chieu: Trai doi ten thu muc, Phai sua 1 file ben trong (Xoa/Sua giu ban da sua)", SyncMode.TwoWay, ConflictPolicy.NewerWins,
            s => s.RenameDir("big", "big2"), s => s.Edit("big/f10.txt", "BAN-DA-SUA-O-PHAI"),
            () =>
            {
                var t = Expect(s => s.RenameDir("big", "big2"));
                t["big/f10.txt"] = "BAN-DA-SUA-O-PHAI";
                return t;
            });

        return (_pass, _fail);
    }
}
