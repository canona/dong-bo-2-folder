using System.Security.Cryptography;
using System.Text;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// HARD-A1: fuzz co seed theo MO HINH THAM CHIEU DOC LAP (bo test kho giai doan 2).
// Bo sinh thao tac ngau nhien (seed co dinh) ap len 2 folder that, sau moi cum 1-6 buoc thi dong bo bang
// SyncSession that, roi doi chieu voi mo hinh duoc viet RIENG trong file nay: mo hinh chi biet
// "noi dung tung duong dan o Trai / Phai / lan dong bo truoc" + quy tac cua che do - khong dung
// ThreeWayAnalyzer / RenameDetector / ConflictResolver. Doi ten, di chuyen thu muc, doi file<->thu muc
// deu duoc mo hinh coi la xoa + tao, nen ghep Move sai (sai noi dung) se lech mo hinh.
//
// Moi buoc cua mot cau hinh (Backup / Mirror / 2 chieu x NewerWins, LeftWins, RightWins) kiem 5 bat bien:
//   MODEL  noi dung 2 phia khop mo hinh        NOOP   dong bo lan 2 = 0 hanh dong
//   TMP    khong sot *.tmp-sync                STATE  state khop quet dia doc lap (size + SHA-256)
//   RUN    khong huy (chot an toan) / khong loi I/O trong lan chay
// FAIL in: seed, cau hinh, buoc, cum thao tac, duong dan lien quan. Sau khi lech, mo hinh duoc dong bo lai
// tu dia de cac loi doc lap khac van duoc tim thay (khong lan truyen mot loi thanh hang tram).
//
// Gioi han da biet (CLAUDE.md "Bay da biet") duoc mo hinh hoa thay vi giau: thao tac "sua giu nguyen
// size + mtime" khong the phat hien -> duong dan do bi DONG BANG (chi chap nhan engine khong dung vao no).
// Moi mtime dat tuong minh (moi buoc cach nhau 10 giay) de ket qua khong phu thuoc dong ho + dung sai 2 giay.
public static class HardMonkeyTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    public const long DefaultSeed = 20261015;
    public const int DefaultSteps = 2000;
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddDays(30);

    private sealed record Cfg(string Name, SyncMode Mode, ConflictPolicy Policy);

    private static readonly Cfg[] Configs =
    {
        new("Backup", SyncMode.BackupOneWay, ConflictPolicy.NewerWins),
        new("Mirror", SyncMode.MirrorOneWay, ConflictPolicy.NewerWins),
        new("TwoWay-NewerWins", SyncMode.TwoWay, ConflictPolicy.NewerWins),
        new("TwoWay-LeftWins", SyncMode.TwoWay, ConflictPolicy.LeftWins),
        new("TwoWay-RightWins", SyncMode.TwoWay, ConflictPolicy.RightWins),
    };

    public static (int pass, int fail) Run()
    {
        // Bien moi truong la cach chinh (HARD_MONKEY_STEPS / HARD_MONKEY_SEED); doi so dong lenh
        // "--monkey-steps=N" / "--monkey-seed=N" chi la du phong cho shell khong dat duoc bien moi truong.
        string? Arg(string key) => Environment.GetCommandLineArgs().Select(a => a.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == key).Select(p => p[1]).FirstOrDefault();
        long seed = long.TryParse(Environment.GetEnvironmentVariable("HARD_MONKEY_SEED") ?? Arg("--monkey-seed"), out var s) ? s : DefaultSeed;
        int steps = int.TryParse(Environment.GetEnvironmentVariable("HARD_MONKEY_STEPS") ?? Arg("--monkey-steps"), out var n) && n > 0 ? n : DefaultSteps;
        Console.WriteLine($"\n--- HARD-A1: fuzz co seed + mo hinh tham chieu doc lap (seed={seed}, {steps} buoc/cau hinh) ---");

        foreach (var cfg in Configs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = new Monkey(cfg, seed, steps);
            m.Execute();
            string head = $"A1 {cfg.Name} (seed={seed}, {m.Step} buoc, {m.Syncs} lan dong bo, {sw.Elapsed.TotalSeconds:0.#}s)";
            foreach (var kind in new[] { "MODEL", "NOOP", "TMP", "STATE", "RUN" })
            {
                var label = kind switch
                {
                    "MODEL" => "ket qua 2 phia khop mo hinh tham chieu",
                    "NOOP" => "dong bo lan 2 = 0 hanh dong",
                    "TMP" => "khong sot *.tmp-sync",
                    "STATE" => "state khop quet dia doc lap",
                    _ => "khong huy / khong loi I/O"
                };
                if (m.Violations.TryGetValue(kind, out var v))
                    Check($"{head}: {label}", false, $"{v.Count} lan vi pham; dau tien: {v.First}");
                else
                    Check($"{head}: {label}", true);
            }
            m.Cleanup();
        }
        return (_pass, _fail);
    }

    private sealed record MFile(string Content, DateTime Mtime);

    private sealed class Violation
    {
        public int Count;
        public string First = "";
    }

    private sealed class Monkey
    {
        private readonly Cfg _cfg;
        private readonly long _seed;
        private readonly int _total;
        private readonly Random _rng;
        private readonly string _left, _right, _dbDir;
        private readonly SqliteStateStore _store;
        private readonly SyncSession _session;
        private const string JobId = "hard-monkey";

        // Mo hinh: noi dung hien tai moi phia + noi dung o lan dong bo thanh cong truoc (B)
        private Dictionary<string, MFile> _l = new(StringComparer.Ordinal), _r = new(StringComparer.Ordinal);
        private Dictionary<string, string> _b = new(StringComparer.Ordinal);
        private readonly HashSet<string> _frozen = new(StringComparer.Ordinal);   // sua giu size+mtime: engine khong thay
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);  // da bi thao tac trong cum nay
        private readonly HashSet<string> _swapLocked = new(StringComparer.Ordinal); // dang doi file<->thu muc trong cum nay
        private readonly List<string> _clusterOps = new();
        private int _counter;

        public int Step { get; private set; }
        public int Syncs { get; private set; }
        public Dictionary<string, Violation> Violations { get; } = new();

        public Monkey(Cfg cfg, long seed, int total)
        {
            _cfg = cfg; _seed = seed; _total = total;
            _rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
            var baseDir = Path.Combine(Path.GetTempPath(), $"foldersync-hard-a1-{cfg.Name}-{Guid.NewGuid():N}");
            _left = Path.Combine(baseDir, "L");
            _right = Path.Combine(baseDir, "R");
            _dbDir = Path.Combine(baseDir, "db");
            Directory.CreateDirectory(_left); Directory.CreateDirectory(_right); Directory.CreateDirectory(_dbDir);
            _store = new SqliteStateStore(Path.Combine(_dbDir, "s.db"));
            _store.UpsertJob(JobId, cfg.Name, _left, _right, cfg.Mode, cfg.Policy, new JobOptions().ToJson());
            _session = new SyncSession(JobId, _left, _right, cfg.Mode, _store, policy: cfg.Policy, scanCache: new ScanCache());
        }

        public void Cleanup()
        {
            _store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(_left)!, true); } catch (Exception) { /* Program.cs don sau */ }
        }

        // ---------------- tien ich ----------------
        private string Root(bool left) => left ? _left : _right;
        private Dictionary<string, MFile> M(bool left) => left ? _l : _r;
        private static string SideName(bool left) => left ? "Trai" : "Phai";
        private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        private DateTime Stamp(int extraSec = 0) => T1.AddSeconds(Step * 10 + extraSec);

        private static void WriteDisk(string root, string rel, string content, DateTime mtime)
        {
            var full = Full(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // thu muc rong con sot (nguoi dung xoa het file ben trong) ma nay lai tao FILE trung ten
            if (Directory.Exists(full) && !Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(full, true);
            File.WriteAllText(full, content);
            File.SetLastWriteTimeUtc(full, mtime);
        }

        private void Put(bool left, string rel, string content, DateTime mtime)
        {
            WriteDisk(Root(left), rel, content, mtime);
            M(left)[rel] = new MFile(content, mtime);
            _touched.Add(rel);
        }

        private static string Abbr(string? c) => c is null ? "<vang>" : $"\"{(c.Length > 14 ? c[..14] + ".." : c)}\"({c.Length}B)";

        private string NewContent()
        {
            // 30% noi dung lay tu bo chung (nhieu file trung noi dung - ep RenameDetector vao ca mo ho);
            // bo chung co nhieu file cung do dai nhung khac noi dung
            if (_rng.Next(100) < 30) return PoolContent(_rng.Next(40));
            int len = 5 + _rng.Next(80);
            return $"u{_seed % 1000}-{++_counter}-" + new string((char)('a' + _rng.Next(26)), len);
        }

        private static string PoolContent(int k) => $"pool{k:00}-" + new string((char)('A' + k % 26), 20 + (k % 8) * 7);

        private static string Flip(string c)
        {
            // Doi 1 ky tu, giu nguyen do dai (noi dung bat buoc khac)
            var arr = c.ToCharArray();
            int i = arr.Length - 1;
            arr[i] = arr[i] == '#' ? '%' : '#';
            return new string(arr);
        }

        private T Pick<T>(IReadOnlyList<T> list) => list[_rng.Next(list.Count)];

        private HashSet<string> Union()
        {
            var u = new HashSet<string>(_l.Keys, StringComparer.Ordinal);
            u.UnionWith(_r.Keys);
            return u;
        }

        private static string? Parent(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : null;

        /// <summary>Thu muc dang co file (hop 2 phia), tru ten trung voi 1 duong dan FILE o phia kia (vua doi file<->thu muc).</summary>
        private List<string> AllDirs()
        {
            var u = Union();
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in u)
                for (var d = Parent(p); d is not null; d = Parent(d)) dirs.Add(d);
            dirs.ExceptWith(u);
            return dirs.OrderBy(d => d, StringComparer.Ordinal).ToList();
        }

        /// <summary>Duong dan hop le = khong xung dot kieu (file cha / con) voi bat ky duong dan nao dang co o 2 phia.</summary>
        private bool PathOk(string path, bool allowExisting = false)
        {
            var u = Union();
            if (!allowExisting && u.Contains(path)) return false;
            for (var d = Parent(path); d is not null; d = Parent(d))
                if (u.Contains(d)) return false;
            var prefix = path + "/";
            return !u.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
        }

        private string? NewPath()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var dirs = AllDirs();
                int roll = _rng.Next(100);
                string dir;
                if (roll < 70 && dirs.Count > 0) dir = Pick(dirs);
                else if (roll < 90 || dirs.Count == 0) dir = $"n{++_counter}";
                else dir = $"{Pick(dirs)}/n{++_counter}";
                var p = $"{dir}/g{++_counter}.txt";
                if (PathOk(p)) return p;
            }
            return null;
        }

        private List<string> Files(bool left)
            => M(left).Keys.Where(p => !_frozen.Contains(p) && !_swapLocked.Contains(p))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();

        // ---------------- cay ban dau ----------------
        private void BuildInitialTree()
        {
            var dirs = new List<string>();
            for (int i = 0; i < 20; i++) dirs.Add($"d{i:00}");
            for (int i = 0; i < 8; i++) dirs.Add($"d{i:00}/s{i % 3}");
            for (int i = 0; i < 200; i++)
            {
                var p = $"{dirs[i % dirs.Count]}/f{i:000}.txt";
                var c = i % 10 < 3 ? PoolContent(i % 40) : NewContent();
                // mtime cach nhau 10 giay: NewerWins khong bao gio gap hoa, HashPlanner khong gap dung sai 2 giay
                var mt = T0.AddSeconds(i * 10);
                WriteDisk(_left, p, c, mt);
                _l[p] = new MFile(c, mt);
            }
        }

        // ---------------- thao tac ----------------
        private string? OpCreate()
        {
            bool left = _rng.Next(2) == 0;
            var p = NewPath();
            if (p is null) return null;
            Put(left, p, NewContent(), Stamp());
            return $"tao {SideName(left)} {p}";
        }

        private string? OpDupCreate()
        {
            var p = NewPath();
            if (p is null) return null;
            var c1 = NewContent();
            var c2 = _rng.Next(100) switch { < 40 => c1, < 70 => Flip(c1), _ => NewContent() };
            Put(true, p, c1, Stamp());
            Put(false, p, c2, Stamp(5)); // lech 5 giay: lech han dung sai 2 giay, khong hoa mtime
            return $"tao trung ten 2 phia {p} ({(c1 == c2 ? "cung noi dung" : c1.Length == c2.Length ? "khac noi dung cung size" : "khac noi dung")})";
        }

        private string? OpModify(bool sameSize)
        {
            bool left = _rng.Next(2) == 0;
            var files = Files(left);
            if (files.Count == 0) return null;
            var p = Pick(files);
            var old = M(left)[p].Content;
            string nc;
            if (sameSize) nc = Flip(old);
            else
            {
                do nc = NewContent(); while (nc.Length == old.Length);
            }
            Put(left, p, nc, Stamp());
            return $"{(sameSize ? "sua giu size" : "sua")} {SideName(left)} {p}";
        }

        /// <summary>Sua noi dung nhung dat lai mtime cu: dung moi tinh khoi doc noi dung -> dong bang duong dan.</summary>
        private string? OpSilent()
        {
            bool left = _rng.Next(2) == 0;
            var cands = Files(left).Where(p => !_touched.Contains(p) && _l.TryGetValue(p, out var a) && _r.TryGetValue(p, out var b)
                && a.Content == b.Content && a.Mtime == b.Mtime && _b.TryGetValue(p, out var bc) && bc == a.Content).ToList();
            if (cands.Count == 0) return null;
            var p = Pick(cands);
            var f = M(left)[p];
            var nc = Flip(f.Content);
            WriteDisk(Root(left), p, nc, f.Mtime);
            M(left)[p] = f with { Content = nc };
            _frozen.Add(p);
            _touched.Add(p);
            return $"sua giu size+mtime {SideName(left)} {p} (engine khong the thay)";
        }

        private string? OpDelete()
        {
            bool left = _rng.Next(2) == 0;
            if (Union().Count < 120) return null; // giu cay ~200 file, tranh chot xoa hang loat huy nham
            var files = Files(left);
            if (files.Count == 0) return null;
            var p = Pick(files);
            File.Delete(Full(Root(left), p));
            M(left).Remove(p);
            _touched.Add(p);
            return $"xoa {SideName(left)} {p}";
        }

        private string? OpRename()
        {
            bool left = _rng.Next(2) == 0;
            var files = Files(left);
            if (files.Count == 0) return null;
            var p = Pick(files);
            string? dest;
            if (_rng.Next(100) < 20)
            {
                // doi ten DE LEN duong dan dang co o phia kia (tao trung ten 2 phia qua doi ten)
                var others = Files(!left).Where(q => !M(left).ContainsKey(q) && q != p && PathOk(q, allowExisting: true)).ToList();
                dest = others.Count == 0 ? null : Pick(others);
            }
            else dest = NewPath();
            if (dest is null || dest == p || M(left).ContainsKey(dest)) return null;
            var src = Full(Root(left), p);
            var dst = Full(Root(left), dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst);
            var f = M(left)[p];
            M(left).Remove(p);
            M(left)[dest] = f; // File.Move giu mtime
            _touched.Add(p); _touched.Add(dest);
            return $"doi ten {SideName(left)} {p} -> {dest}";
        }

        private string? OpDirMove()
        {
            bool left = _rng.Next(2) == 0;
            var dirs = AllDirs().Where(d => M(left).Keys.Any(k => k.StartsWith(d + "/", StringComparison.Ordinal))).ToList();
            if (dirs.Count == 0) return null;
            var d = Pick(dirs);
            var inside = M(left).Keys.Where(k => k.StartsWith(d + "/", StringComparison.Ordinal)).ToList();
            if (inside.Any(k => _frozen.Contains(k) || _swapLocked.Contains(k))) return null;
            var all = AllDirs();
            var parent = _rng.Next(100) < 60 && all.Count > 0 ? Pick(all) : null;
            if (parent is not null && (parent == d || parent.StartsWith(d + "/", StringComparison.Ordinal))) parent = null;
            var np = (parent is null ? "" : parent + "/") + $"mv{++_counter}";
            if (!PathOk(np + "/x")) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(Full(Root(left), np))!);
            Directory.Move(Full(Root(left), d), Full(Root(left), np));
            foreach (var k in inside)
            {
                var f = M(left)[k];
                M(left).Remove(k);
                var nk = np + k[d.Length..];
                M(left)[nk] = f;
                _touched.Add(k); _touched.Add(nk);
            }
            return $"doi ten thu muc {SideName(left)} {d} -> {np} ({inside.Count} file)";
        }

        private bool Clean(string p)
            => _l.TryGetValue(p, out var a) && _r.TryGetValue(p, out var b) && a.Content == b.Content
               && _b.TryGetValue(p, out var bc) && bc == a.Content
               && !_touched.Contains(p) && !_frozen.Contains(p) && !_swapLocked.Contains(p);

        /// <summary>File -> thu muc cung ten tren 1 phia (phia kia van la file cu, chua dong bo).</summary>
        private string? OpFileToDir()
        {
            if (_cfg.Mode == SyncMode.BackupOneWay) return null; // Backup khong xoa o dich: kieu duong dan khong the hoi tu
            bool left = _rng.Next(2) == 0;
            var cands = _l.Keys.Where(Clean).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (cands.Count == 0) return null;
            var p = Pick(cands);
            var inner = p + "/s.txt";
            File.Delete(Full(Root(left), p));
            var f = M(left)[p];
            M(left).Remove(p);
            var mf = new MFile(NewContent(), Stamp());
            WriteDisk(Root(left), inner, mf.Content, mf.Mtime);
            M(left)[inner] = mf;
            _swapLocked.Add(p); _swapLocked.Add(inner); _touched.Add(p); _touched.Add(inner);
            return $"file -> thu muc {SideName(left)} {p} (thanh {inner})";
        }

        /// <summary>Thu muc -> file cung ten tren 1 phia (phia kia van la thu muc day file cu).</summary>
        private string? OpDirToFile()
        {
            if (_cfg.Mode == SyncMode.BackupOneWay) return null;
            bool left = _rng.Next(2) == 0;
            var dirs = AllDirs().Where(d =>
            {
                var pre = d + "/";
                var a = _l.Keys.Where(k => k.StartsWith(pre, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var b = _r.Keys.Where(k => k.StartsWith(pre, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                return a.Count > 0 && a.SequenceEqual(b) && a.All(Clean);
            }).ToList();
            if (dirs.Count == 0) return null;
            var d = Pick(dirs);
            var inside = M(left).Keys.Where(k => k.StartsWith(d + "/", StringComparison.Ordinal)).ToList();
            Directory.Delete(Full(Root(left), d), true);
            foreach (var k in inside) { M(left).Remove(k); _swapLocked.Add(k); _touched.Add(k); }
            var mf = new MFile(NewContent(), Stamp());
            WriteDisk(Root(left), d, mf.Content, mf.Mtime);
            M(left)[d] = mf;
            _swapLocked.Add(d); _touched.Add(d);
            return $"thu muc -> file {SideName(left)} {d} ({inside.Count} file bi thay)";
        }

        private (string Name, int Weight, Func<string?> Do)[] OpTable() =>
        [
            ("create", 12, OpCreate), ("modify", 14, () => OpModify(false)), ("samesize", 10, () => OpModify(true)),
            ("silent", 3, OpSilent), ("delete", 10, OpDelete), ("rename", 10, OpRename), ("dirmove", 3, OpDirMove),
            ("dupcreate", 5, OpDupCreate), ("file2dir", 3, OpFileToDir), ("dir2file", 3, OpDirToFile),
        ];

        private void DoOneOp()
        {
            var table = OpTable();
            int totalW = table.Sum(t => t.Weight);
            for (int attempt = 0; attempt < 40; attempt++)
            {
                int roll = _rng.Next(totalW);
                foreach (var t in table)
                {
                    if (roll < t.Weight)
                    {
                        var desc = t.Do();
                        if (desc is not null) { _clusterOps.Add($"#{Step} {desc}"); return; }
                        break;
                    }
                    roll -= t.Weight;
                }
            }
            _clusterOps.Add($"#{Step} (khong chon duoc thao tac)");
        }

        // ---------------- MO HINH THAM CHIEU (khong dung engine) ----------------
        private (Dictionary<string, MFile> L, Dictionary<string, MFile> R) Predict()
        {
            var nl = new Dictionary<string, MFile>(_l, StringComparer.Ordinal);
            var nr = new Dictionary<string, MFile>(_r, StringComparer.Ordinal);
            var paths = _l.Keys.Concat(_r.Keys).Concat(_b.Keys).Distinct().OrderBy(p => p, StringComparer.Ordinal);
            foreach (var p in paths)
            {
                if (_frozen.Contains(p)) continue; // engine tin 2 phia giong nhau -> khong dong vao
                _l.TryGetValue(p, out var l);
                _r.TryGetValue(p, out var r);
                _b.TryGetValue(p, out var b);
                switch (_cfg.Mode)
                {
                    case SyncMode.BackupOneWay:
                        if (l is not null) nr[p] = l;          // nguon la chuan; nguon xoa thi dich giu nguyen
                        break;
                    case SyncMode.MirrorOneWay:
                        if (l is not null) nr[p] = l; else nr.Remove(p);
                        break;
                    default:
                        PredictTwoWay(p, l, r, b, nl, nr);
                        break;
                }
            }
            return (nl, nr);
        }

        private void PredictTwoWay(string p, MFile? l, MFile? r, string? b,
            Dictionary<string, MFile> nl, Dictionary<string, MFile> nr)
        {
            if (l is null && r is null) return;
            if (l is not null && r is not null)
            {
                if (l.Content == r.Content) return;
                if (b is null) { Conflict(p, l, r, nl, nr); return; }
                bool lc = l.Content != b, rc = r.Content != b;
                if (lc && !rc) nr[p] = l;
                else if (!lc && rc) nl[p] = r;
                else Conflict(p, l, r, nl, nr);
                return;
            }
            var present = l ?? r!;
            bool leftHas = l is not null;
            if (b is null || present.Content != b)
            {
                // moi tao, hoac xoa/sua: giu ban con lai (ban da sua), chep sang phia thieu
                if (leftHas) nr[p] = present; else nl[p] = present;
            }
            else
            {
                // phia kia xoa, phia nay khong doi -> lan truyen xoa
                if (leftHas) nl.Remove(p); else nr.Remove(p);
            }
        }

        private void Conflict(string p, MFile l, MFile r, Dictionary<string, MFile> nl, Dictionary<string, MFile> nr)
        {
            bool leftWins;
            switch (_cfg.Policy)
            {
                case ConflictPolicy.LeftWins: leftWins = true; break;
                case ConflictPolicy.RightWins: leftWins = false; break;
                case ConflictPolicy.NewerWins:
                    if (l.Mtime == r.Mtime) throw new InvalidOperationException($"bo sinh tao mtime bang nhau o {p} (loi cua test)");
                    leftWins = l.Mtime > r.Mtime;
                    break;
                default: throw new InvalidOperationException("chinh sach khong ho tro trong A1");
            }
            if (leftWins) nr[p] = l; else nl[p] = r;
        }

        // ---------------- doc dia doc lap ----------------
        private static Dictionary<string, MFile> ReadDisk(string root, List<string> tmp)
        {
            var map = new Dictionary<string, MFile>(StringComparer.Ordinal);
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.StartsWith(".foldersync_history/", StringComparison.Ordinal)) continue;
                if (rel.EndsWith(".tmp-sync", StringComparison.Ordinal)) { tmp.Add(rel); continue; }
                map[rel] = new MFile(File.ReadAllText(full), File.GetLastWriteTimeUtc(full));
            }
            return map;
        }

        private void Violate(string kind, string detail)
        {
            if (!Violations.TryGetValue(kind, out var v)) Violations[kind] = v = new Violation();
            v.Count++;
            if (v.Count == 1)
                v.First = $"seed={_seed} cau hinh={_cfg.Name} buoc={Step}/{_total} thao tac cuoi='{_clusterOps.LastOrDefault() ?? "(cay ban dau)"}' " +
                          $"cum=[{string.Join(" | ", _clusterOps)}] {detail}";
        }

        private void SyncAndCheck()
        {
            var predicted = Predict();
            SessionOutcome outcome;
            try { outcome = _session.Run(); }
            catch (Exception ex)
            {
                Violate("RUN", $"ngoai le {ex.GetType().Name}: {ex.Message}");
                Syncs++;
                Resync();
                return;
            }
            Syncs++;

            if (outcome.Result is { Aborted: true } ab) Violate("RUN", $"bi huy: {ab.AbortReason}");
            if (outcome.Result is { } rr && rr.Errors.Count > 0) Violate("RUN", $"{rr.Errors.Count} loi I/O: {string.Join("; ", rr.Errors.Take(3))}");

            var tmp = new List<string>();
            var dl = ReadDisk(_left, tmp);
            var dr = ReadDisk(_right, tmp);
            if (tmp.Count > 0) Violate("TMP", $"file tam sot lai: {string.Join(", ", tmp.Take(5))}");

            var diffs = new List<string>();
            foreach (var p in predicted.L.Keys.Concat(dl.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                predicted.L.TryGetValue(p, out var e); dl.TryGetValue(p, out var a);
                if (e?.Content != a?.Content) diffs.Add($"Trai {p}: mong doi {Abbr(e?.Content)} thuc te {Abbr(a?.Content)}");
            }
            foreach (var p in predicted.R.Keys.Concat(dr.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                predicted.R.TryGetValue(p, out var e); dr.TryGetValue(p, out var a);
                if (e?.Content != a?.Content) diffs.Add($"Phai {p}: mong doi {Abbr(e?.Content)} thuc te {Abbr(a?.Content)}");
            }
            if (diffs.Count > 0) Violate("MODEL", $"{diffs.Count} duong dan lech, vi du: {string.Join(" ;; ", diffs.Take(4))}");

            // Mo hinh dong bo lai tu dia (de ca khi lech van tiep tuc tim loi doc lap khac)
            _l = dl; _r = dr;
            var nb = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (p, f) in _l) if (_r.TryGetValue(p, out var g) && g.Content == f.Content) nb[p] = f.Content;
            foreach (var p in _frozen) if (_b.TryGetValue(p, out var old)) nb[p] = old; else nb.Remove(p);
            _b = nb;

            CheckState(dl, dr);

            var again = _session.AnalyzeOnly();
            var work = again.Actions.Where(x => x.Kind != SyncActionKind.Skip).ToList();
            if (work.Count > 0)
                Violate("NOOP", $"dong bo lan 2 con {work.Count} hanh dong: {string.Join(" ;; ", work.Take(4).Select(w => $"{w.Kind} {w.RelativePath}" + (w.TargetPath is null ? "" : $" -> {w.TargetPath}") + $" ({w.Reason})"))}");
        }

        /// <summary>State phai = dung tap duong dan 2 phia giong nhau (size + SHA-256 tinh lai tu dia), khong hon khong kem.</summary>
        private void CheckState(Dictionary<string, MFile> dl, Dictionary<string, MFile> dr)
        {
            var state = _store.LoadState(JobId);
            var problems = new List<string>();
            var expected = dl.Keys.Where(p => dr.TryGetValue(p, out var g) && g.Content == dl[p].Content && !_frozen.Contains(p));
            foreach (var p in expected.OrderBy(x => x, StringComparer.Ordinal))
                if (!state.ContainsKey(p)) problems.Add($"thieu trong state: {p}");
            foreach (var (p, e) in state.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (_frozen.Contains(p)) continue;
                if (!dl.TryGetValue(p, out var l) || !dr.TryGetValue(p, out var r) || l.Content != r.Content)
                {
                    problems.Add($"state noi doi (2 phia khong giong nhau): {p}");
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes(l.Content);
                if (e.Size != bytes.Length) problems.Add($"state sai size {p}: {e.Size} vs {bytes.Length}");
                else if (e.Hash is not null && !string.Equals(e.Hash, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                    problems.Add($"state sai hash {p}");
            }
            if (problems.Count > 0) Violate("STATE", $"{problems.Count} loi, vi du: {string.Join(" ;; ", problems.Take(4))}");
        }

        private void Resync() { /* sau ngoai le: dua mo hinh ve dia thuc */
            var tmp = new List<string>();
            _l = ReadDisk(_left, tmp); _r = ReadDisk(_right, tmp);
            _b = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (p, f) in _l) if (_r.TryGetValue(p, out var g) && g.Content == f.Content) _b[p] = f.Content;
        }

        public void Execute()
        {
            BuildInitialTree();
            _clusterOps.Clear();
            _clusterOps.Add("dong bo lan dau");
            SyncAndCheck();
            while (Step < _total)
            {
                _touched.Clear(); _swapLocked.Clear(); _clusterOps.Clear();
                int n = Math.Min(1 + _rng.Next(6), _total - Step);
                for (int i = 0; i < n; i++) { Step++; DoOneOp(); }
                SyncAndCheck();
            }
        }
    }
}
