using System.Text;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;
using Microsoft.Data.Sqlite;

// Bo test kho giai doan 3: HARD-E4 (sua nhanh + trung mtime), HARD-F1 (bit rot), HARD-H1 (DB hong), HARD-D2 (2 Job chung dich).
// Oracle doc lap: quet lai tu dia (FolderScanner computeHashes, khong cache) - khong tin state/cache cua engine.
// Dong "[INFO]" la ghi nhan hanh vi (khong tinh PASS/FAIL) de dua vao bao cao PHAT HIEN.
public static class TimeCorruptionHardTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD3 {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static void Info(string text) => Console.WriteLine("[INFO] HARD3 " + text);

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-hard3-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteBytes(string root, string rel, byte[] data, DateTime? mtime = null)
    {
        var full = Full(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        if (mtime is { } t) File.SetLastWriteTimeUtc(full, t);
    }

    private static void WriteText(string root, string rel, string content) => WriteBytes(root, rel, Encoding.UTF8.GetBytes(content));

    private static byte[] Rand(Random rnd, int size) { var b = new byte[size]; rnd.NextBytes(b); return b; }

    private static string Sha(string path) => FileHasher.ComputeHash(path);

    private static (SqliteStateStore store, string dbPath) NewStore(string label)
    {
        var dbPath = Path.Combine(NewTempDir(label + "-db"), "s.db");
        return (new SqliteStateStore(dbPath), dbPath);
    }

    private static JobRecord AddJob(SqliteStateStore store, string label, string l, string r, SyncMode mode, JobOptions o)
    {
        var id = "job-" + label + "-" + Guid.NewGuid().ToString("N")[..6];
        store.UpsertJob(id, label, l, r, mode, ConflictPolicy.NewerWins, o.ToJson());
        return store.ListJobs().Single(j => j.Id == id);
    }

    private static SyncSession Session(string id, string l, string r, SyncMode mode, SqliteStateStore store,
        bool hashAll, SyncOptions? opt = null)
        => new(id, l, r, mode, store, opt, ConflictPolicy.NewerWins, hashAllFiles: hashAll);

    private static SessionOutcome? RunJob(JobRunner r, string trigger) => r.RunAsync(trigger).GetAwaiter().GetResult();

    /// <summary>Quet doc lap: relPath -> hash that tren dia (khong cache, bo .foldersync_history).</summary>
    private static Dictionary<string, string?> Truth(string root)
        => new FolderScanner().Scan(root, computeHashes: true).Entries.ToDictionary(e => e.Key, e => e.Value.Hash);

    /// <summary>State "noi doi": entry co trong state ma 2 phia tren dia khong giong nhau (size/hash) hoac khac hash trong state.</summary>
    private static List<string> StateLies(SqliteStateStore store, string jobId, string left, string right)
    {
        var l = new FolderScanner().Scan(left, computeHashes: true).Entries;
        var r = new FolderScanner().Scan(right, computeHashes: true).Entries;
        var lies = new List<string>();
        foreach (var (path, s) in store.LoadState(jobId))
        {
            if (!l.TryGetValue(path, out var le) || !r.TryGetValue(path, out var re)) { lies.Add($"{path}: thieu 1 phia"); continue; }
            if (le.Size != re.Size || le.Hash != re.Hash) lies.Add($"{path}: 2 phia khac nhau nhung state coi la giong");
            else if (s.Hash is not null && s.Hash != le.Hash) lies.Add($"{path}: hash state != hash that");
        }
        return lies;
    }

    private static int CountFiles(string root)
        => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Count(f => !f.Contains(".foldersync_history")) : 0;

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- HARD 3: E4 sua nhanh + trung mtime / F1 bit rot / H1 DB hong / D2 2 Job chung dich ---");
        E4RapidEdits();
        F1BitRot();
        H1CorruptDb();
        D2SharedDestination();
        return (_pass, _fail);
    }

    // ============================ HARD-E4 ============================
    // 100 lan sua 1 file, size KHONG doi, moi lan chay dong bo ngay. Cu 4 lan co 1 lan dat mtime Trai = mtime ban o Dich
    // (sau lan sync truoc, Dich mang mtime cua Trai -> "trung het"). Oracle: noi dung Dich == Trai sau MOI lan chay.
    private sealed record E4Result(int Undetected, int UndetectedCollision, int UndetectedPlain, List<string> Lies, string First);

    private static E4Result E4Loop(string label, bool viaRunner, bool hashAll)
    {
        const int Edits = 100, Size = 4096;
        var rnd = new Random(20261017);
        var left = NewTempDir(label + "-l"); var right = NewTempDir(label + "-r");
        var (store, _) = NewStore(label);
        using (store)
        {
            var o = new JobOptions { FullContentCompare = hashAll, CleanupHistory = false };
            var job = AddJob(store, label, left, right, SyncMode.BackupOneWay, o);
            using var runner = viaRunner ? new JobRunner(job, store) : null;
            var t0 = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            WriteBytes(left, "f.bin", Rand(rnd, Size), t0);
            void Sync() { if (runner is not null) RunJob(runner, "e4"); else Session(job.Id, left, right, job.Mode, store, hashAll).Run(); }
            Sync();

            DateTime lastMtime = t0;
            int undetected = 0, undetCollision = 0, undetPlain = 0; var lies = new List<string>(); string first = "";
            for (int i = 1; i <= Edits; i++)
            {
                bool collide = i % 4 == 0;
                var data = Rand(rnd, Size);
                // collide: dat lai dung mtime cu (= mtime ban o Dich); nguoc lai mtime moi, khac han
                var mtime = collide ? lastMtime : t0.AddSeconds(i * 7);
                WriteBytes(left, "f.bin", data, mtime);
                lastMtime = mtime;
                Sync();
                bool same = File.ReadAllBytes(Full(right, "f.bin")).AsSpan().SequenceEqual(data);
                if (!same)
                {
                    undetected++;
                    if (collide) undetCollision++; else undetPlain++;
                    if (first.Length == 0) first = $"lan sua {i} (trung mtime={collide})";
                }
                lies.AddRange(StateLies(store, job.Id, left, right).Select(x => $"lan {i}: {x}"));
            }
            return new E4Result(undetected, undetCollision, undetPlain, lies, first);
        }
    }

    private static void E4RapidEdits()
    {
        var a = E4Loop("e4a", viaRunner: false, hashAll: true);
        Check("E4 100 lan sua cung size (25 lan trung mtime), phien moi + hash toan bo: Dich == Trai sau MOI lan, state khong noi doi",
            a.Undetected == 0 && a.Lies.Count == 0,
            $"bo sot {a.Undetected}/100 (trung mtime {a.UndetectedCollision}), dau tien: {a.First}; state noi doi {a.Lies.Count}: {a.Lies.FirstOrDefault()}");

        var b = E4Loop("e4b", viaRunner: true, hashAll: true);
        Check("E4 nhu tren qua duong that JobRunner + FullContentCompare (cache hash giu qua cac lan chay): phat hien du 100/100, state khong noi doi",
            b.Undetected == 0 && b.Lies.Count == 0,
            $"bo sot {b.Undetected}/100 (trung mtime {b.UndetectedCollision}, mtime khac {b.UndetectedPlain}), dau tien: {b.First}; state noi doi {b.Lies.Count}: {b.Lies.FirstOrDefault()}");

        var c = E4Loop("e4c", viaRunner: true, hashAll: false);
        Check("E4 che do mac dinh (khong FullContentCompare): lan sua co mtime KHAC luon phat hien (75/75)",
            c.UndetectedPlain == 0, $"bo sot {c.UndetectedPlain} lan mtime khac");
        Info($"E4 mac dinh (khong FullContentCompare): bo sot {c.UndetectedCollision}/25 lan trung mtime (gioi han da biet - HashPlanner coi cung size+mtime la giong); " +
             $"state noi doi: {c.Lies.Count}");
    }

    // ============================ HARD-F1 ============================
    private static void FlipBit(string path, DateTime mtimeToRestore)
    {
        var data = File.ReadAllBytes(path);
        data[data.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, mtimeToRestore);
    }

    private static void F1BitRot()
    {
        var t0 = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        foreach (var (mode, modeName) in new[] { (SyncMode.BackupOneWay, "Backup"), (SyncMode.MirrorOneWay, "Mirror") })
        {
            // ---- Lat bit o file DICH ----
            foreach (var path in new[] { "phien-moi", "JobRunner" })
            {
                var rnd = new Random(7);
                var left = NewTempDir("f1d-l"); var right = NewTempDir("f1d-r");
                var (store, _) = NewStore("f1d");
                using (store)
                {
                    var job = AddJob(store, "f1d", left, right, mode, new JobOptions { FullContentCompare = true, CleanupHistory = false });
                    for (int i = 0; i < 5; i++) WriteBytes(left, $"d/f{i}.bin", Rand(rnd, 8192), t0.AddSeconds(i));
                    using var runner = path == "JobRunner" ? new JobRunner(job, store) : null;
                    void Sync() { if (runner is not null) RunJob(runner, "f1"); else Session(job.Id, left, right, mode, store, true).Run(); }
                    Sync();
                    var want = Truth(left);
                    var sameBefore = Truth(right).All(kv => want[kv.Key] == kv.Value);

                    var victim = Full(right, "d/f2.bin");
                    var mt = File.GetLastWriteTimeUtc(victim);
                    var size = new FileInfo(victim).Length;
                    FlipBit(victim, mt);
                    bool rotted = Sha(victim) != want["d/f2.bin"] && new FileInfo(victim).Length == size && File.GetLastWriteTimeUtc(victim) == mt;

                    Sync();
                    var after = Truth(right);
                    bool healed = after["d/f2.bin"] == want["d/f2.bin"];
                    var lies = StateLies(store, job.Id, left, right);
                    int history = Directory.Exists(Path.Combine(right, ".foldersync_history"))
                        ? Directory.GetFiles(Path.Combine(right, ".foldersync_history"), "*", SearchOption.AllDirectories).Length : 0;
                    Check($"F1 {modeName}/{path}: lat 1 bit file DICH (giu size+mtime) -> lan chay co hash phat hien va chep lai tu nguon, state khong noi doi, ban hong vao history",
                        sameBefore && rotted && healed && lies.Count == 0 && history >= 1,
                        $"tien-quyet(giong truoc={sameBefore}, da lat dung={rotted}); dich lanh lai={healed}; state noi doi={lies.Count}; history={history}");
                }
            }

            // ---- Lat bit o file NGUON (giu size+mtime) ----
            {
                var rnd = new Random(8);
                var left = NewTempDir("f1s-l"); var right = NewTempDir("f1s-r");
                var (store, _) = NewStore("f1s");
                using (store)
                {
                    var job = AddJob(store, "f1s", left, right, mode, new JobOptions { FullContentCompare = true, CleanupHistory = false });
                    for (int i = 0; i < 5; i++) WriteBytes(left, $"d/f{i}.bin", Rand(rnd, 8192), t0.AddSeconds(i));
                    using var runner = new JobRunner(job, store);
                    RunJob(runner, "f1");
                    var oldHash = Truth(left)["d/f2.bin"];
                    var victim = Full(left, "d/f2.bin");
                    FlipBit(victim, File.GetLastWriteTimeUtc(victim));
                    var newHash = Truth(left)["d/f2.bin"];
                    var plan = runner.AnalyzeAsync().GetAwaiter().GetResult();
                    var act = plan?.Actions.FirstOrDefault(a => a.RelativePath == "d/f2.bin");
                    RunJob(runner, "f1");
                    var destHash = Truth(right)["d/f2.bin"];
                    bool silentlyUnchanged = act is null || act.Kind == SyncActionKind.Skip;
                    Info($"F1 {modeName}: lat bit file NGUON (FullContentCompare, qua JobRunner): hanh dong phan tich={act?.Kind.ToString() ?? "(khong co)"}; " +
                         $"dich {(destHash == newHash ? "da nhan noi dung moi (nguon thang)" : destHash == oldHash ? "giu noi dung cu" : "khac ca hai")}");
                    Check($"F1 {modeName}: lat 1 bit file NGUON (giu size+mtime) khi bat hash: app KHONG am tham coi la 'khong doi'",
                        !silentlyUnchanged && destHash == newHash && newHash != oldHash,
                        $"hanh dong={act?.Kind.ToString() ?? "(khong co)"}, dich khop nguon moi={destHash == newHash}");
                }
            }
        }

        // Che do mac dinh (khong FullContentCompare): chi ghi nhan
        {
            var rnd = new Random(9);
            var left = NewTempDir("f1m-l"); var right = NewTempDir("f1m-r");
            var (store, _) = NewStore("f1m");
            using (store)
            {
                var job = AddJob(store, "f1m", left, right, SyncMode.BackupOneWay, new JobOptions { CleanupHistory = false });
                WriteBytes(left, "f.bin", Rand(rnd, 8192), t0);
                using var runner = new JobRunner(job, store);
                RunJob(runner, "f1");
                var mt = File.GetLastWriteTimeUtc(Full(right, "f.bin"));
                FlipBit(Full(right, "f.bin"), mt);
                RunJob(runner, "f1");
                Info($"F1 mac dinh (KHONG FullContentCompare): bit rot o dich {(Truth(right)["f.bin"] == Truth(left)["f.bin"] ? "duoc chep lai" : "KHONG bi phat hien (gioi han da biet: cung size+mtime coi la giong)")}");
            }
        }
    }

    // ============================ HARD-H1 ============================
    private const int H1Files = 20;

    /// <summary>Tao Job da sync xong, dong DB, tra ve ban sao luu db + thong tin Job.</summary>
    private static (string db, string backup, JobRecord job, string left, string right) H1Make(string label, SyncMode mode, JobOptions o)
    {
        var left = NewTempDir(label + "-l"); var right = NewTempDir(label + "-r");
        var (store, db) = NewStore(label);
        JobRecord job;
        using (store)
        {
            job = AddJob(store, label, left, right, mode, o);
            for (int i = 0; i < H1Files; i++) WriteText(left, $"d{i % 4}/f{i}.txt", $"noi dung {i}");
            var res = new JobRunner(job, store).RunAsync("h1").GetAwaiter().GetResult();
            if (res?.Result?.Copied != H1Files || store.LoadState(job.Id).Count != H1Files)
                throw new InvalidOperationException("H1 tien-quyet: lan sync dau khong dung");
        }
        SqliteConnection.ClearAllPools();
        var backup = db + ".bak";
        File.Copy(db, backup, true);
        return (db, backup, job, left, right);
    }

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Mo lai DB nhu app luc khoi dong roi chay Job nhu nguoi dung bam Dong bo. Tra mo ta ket qua + so file Phai con lai.</summary>
    private static (string outcome, bool crashed, bool deletedAny) H1Reopen(string db, string jobId)
    {
        try
        {
            using var store = new SqliteStateStore(db);
            var jobs = store.ListJobs();
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is null) return ($"mo duoc DB nhung khong con Job ({jobs.Count} Job)", false, false);
            using var runner = new JobRunner(job, store);
            var res = RunJob(runner, "h1");
            var r = res?.Result;
            return ($"chay: copied={r?.Copied} deleted={r?.Deleted} aborted={r?.Aborted} ({r?.AbortReason}) loi={r?.Errors.Count}",
                false, (r?.Deleted ?? 0) > 0);
        }
        catch (Exception ex)
        {
            return ($"nem {ex.GetType().Name}: {ex.Message.Split('\n')[0]}", true, false);
        }
    }

    private static void H1CorruptDb()
    {
        // ---- (a) cat file DB con mot nua ----
        {
            var (db, backup, job, left, right) = H1Make("h1a", SyncMode.MirrorOneWay, new JobOptions { CleanupHistory = false });
            var before = Truth(right);
            using (var fs = new FileStream(db, FileMode.Open)) fs.SetLength(fs.Length / 2);
            var (outcome, _, deleted) = H1Reopen(db, job.Id);
            SqliteConnection.ClearAllPools();
            var after = Truth(right);
            Info($"H1a DB cat con mot nua ({new FileInfo(backup).Length} -> {new FileInfo(db).Length} byte): {outcome}");
            Check("H1a DB bi cat con mot nua: bao loi ro rang HOAC chay an toan - Phai (Mirror) con nguyen " + H1Files + " file, khong xoa gi",
                !deleted && after.Count == before.Count && after.All(kv => before[kv.Key] == kv.Value),
                $"{outcome}; Phai con {after.Count}/{before.Count}");
        }

        // ---- (b) xoa toan bo file_state, giu jobs ----
        foreach (var (mode, name) in new[] { (SyncMode.MirrorOneWay, "Mirror"), (SyncMode.TwoWay, "2 chieu"), (SyncMode.BackupOneWay, "Backup") })
        {
            // Ke hoach: SAU khi state mat, Trai da bi mat nhieu file (12/20 = 60%). Co state thi chot 50% chan (doi chung);
            // mat state thi "chua tung sync" -> moc % = Trai + Phai co the khong chan nua.
            foreach (var wipe in new[] { false, true })
            {
                var (db, _, job, left, right) = H1Make("h1b", mode, new JobOptions { CleanupHistory = false });
                if (wipe) Exec(db, "DELETE FROM file_state;");
                for (int i = 0; i < 12; i++) File.Delete(Full(left, $"d{i % 4}/f{i}.txt"));
                var rightBefore = CountFiles(right);
                var (outcome, _, _) = H1Reopen(db, job.Id);
                SqliteConnection.ClearAllPools();
                int rightAfter = CountFiles(right), leftAfter = CountFiles(left);
                Info($"H1b {name}, {(wipe ? "MAT state" : "con state (doi chung)")}: Trai con {leftAfter}/20; Phai {rightBefore} -> {rightAfter}; {outcome}");
                if (!wipe) continue;
                // Mirror: file Trai xoa that su -> xoa o Phai la dung y nghia Mirror, nhung khong duoc de chot an toan bi vo hieu bang viec mat state.
                bool safe = mode == SyncMode.MirrorOneWay
                    ? rightAfter >= rightBefore - 0 && rightAfter == rightBefore
                    : rightAfter >= rightBefore; // Backup/2 chieu: khong duoc mat file o Phai (2 chieu phai chep nguoc ve Trai)
                Check($"H1b {name}: file_state bi xoa het (jobs con): KHONG xoa file o Phai chi vi suy ra 'chua tung sync' (Phai {rightBefore} -> {rightAfter})",
                    safe, outcome);
            }
        }

        // ---- (c) settings_json bi cat cut thanh JSON sai ----
        {
            // Job co cau hinh an toan rieng: nguong xoa 10%, loai tru "giu/*" (file o Phai trong "giu/" khong thuoc dong bo)
            var o = new JobOptions { CleanupHistory = false, SafetyMaxDeletePercent = 10, ExcludePatterns = "giu/*" };
            var (db, _, job, left, right) = H1Make("h1c", SyncMode.MirrorOneWay, o);
            WriteText(right, "giu/quan-trong.txt", "file rieng cua nguoi dung o Phai, ngoai pham vi Job (bi Exclude)");
            var json = o.ToJson();
            var truncated = json[..(json.Length / 2)];
            Exec(db, $"UPDATE jobs SET settings_json = '{truncated.Replace("'", "''")}';");
            var parsed = JobOptions.FromJson(truncated);
            Info($"H1c settings_json cat cut ({json.Length} -> {truncated.Length} ky tu): FromJson tra mac dinh Job moi: nguong xoa {parsed.SafetyMaxDeletePercent}% (goc 10%), Exclude='{parsed.ExcludePatterns}' (goc 'giu/*')");
            var (outcome, crashed, _) = H1Reopen(db, job.Id);
            SqliteConnection.ClearAllPools();
            bool keep = File.Exists(Full(right, "giu/quan-trong.txt"));
            Info($"H1c: {outcome}; file ngoai pham vi o Phai {(keep ? "con" : "BI XOA khoi Phai")}");
            Check("H1c settings_json hong: bao loi ro rang HOAC chay an toan - khong am tham bo Exclude/nguong xoa roi xoa file o Phai",
                crashed || keep, outcome);
        }
    }

    // ============================ HARD-D2 (characterization, CHI GHI NHAN) ============================
    private static void D2SharedDestination()
    {
        foreach (var (mode, name) in new[] { (SyncMode.BackupOneWay, "Backup"), (SyncMode.MirrorOneWay, "Mirror"), (SyncMode.TwoWay, "2 chieu") })
        {
            var la = NewTempDir("d2a"); var lb = NewTempDir("d2b"); var dest = NewTempDir("d2d");
            for (int i = 0; i < 6; i++) { WriteText(la, $"a{i}.txt", "A" + i); WriteText(lb, $"b{i}.txt", "B" + i); }
            var (store, _) = NewStore("d2");
            using (store)
            {
                var ja = AddJob(store, "A", la, dest, mode, new JobOptions { CleanupHistory = false });
                var jb = AddJob(store, "B", lb, dest, mode, new JobOptions { CleanupHistory = false });
                var rows = new List<string>();
                var exceptions = new List<string>();
                var perRound = new List<int>();
                for (int round = 1; round <= 10; round++)
                {
                    int actionsThisRound = 0;
                    foreach (var j in new[] { ja, jb })
                    {
                        try
                        {
                            using var runner = new JobRunner(j, store);
                            var res = RunJob(runner, "d2")?.Result;
                            int n = (res?.Copied ?? 0) + (res?.Deleted ?? 0) + (res?.Moved ?? 0) + (res?.Conflicts ?? 0);
                            actionsThisRound += n;
                            rows.Add($"v{round}{(j == ja ? "A" : "B")}: chep {res?.Copied} xoa {res?.Deleted} huy={res?.Aborted}");
                        }
                        catch (Exception ex) { exceptions.Add($"v{round}: {ex.GetType().Name}"); }
                    }
                    perRound.Add(actionsThisRound);
                }
                int tmp = Directory.GetFiles(dest, "*.tmp-sync", SearchOption.AllDirectories).Length
                        + Directory.GetFiles(la, "*.tmp-sync", SearchOption.AllDirectories).Length
                        + Directory.GetFiles(lb, "*.tmp-sync", SearchOption.AllDirectories).Length;
                var final = Directory.GetFiles(dest, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Order().ToList();
                bool converged = perRound.Skip(Math.Max(0, perRound.Count - 3)).All(n => n == 0);
                Info($"D2 {name}: so hanh dong moi vong (A+B) = [{string.Join(",", perRound)}]; " +
                     $"{(converged ? "HOI TU (3 vong cuoi = 0)" : "KHONG HOI TU - dao dong")}; Dich cuoi con {final.Count} file {{{string.Join(",", final)}}}");
                Info("D2 " + name + " chi tiet: " + string.Join(" | ", rows));
                Check($"D2 {name} (chi ghi nhan): 10 vong luan phien khong nem loi, khong sot .tmp-sync (xem dong INFO cho ket qua hoi tu/dao dong)",
                    exceptions.Count == 0 && tmp == 0, $"loi={string.Join(",", exceptions)} tmp={tmp}");
            }
        }
    }
}
