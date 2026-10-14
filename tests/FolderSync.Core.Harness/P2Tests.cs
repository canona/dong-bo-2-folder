using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung P2: ConflictResolver (4 chinh sach), quan ly Job, HistoryService khoi phuc.
public static class P2Tests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] P2 {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-p2-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string root, string rel, string content, DateTime? mtimeUtc = null)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (mtimeUtc is { } t) File.SetLastWriteTimeUtc(full, t);
    }

    private static string? Read(string root, string rel)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private static bool Exists(string root, string rel)
        => File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    // Tao 1 cap folder da sync san file chung.txt = "v1" lam baseline
    private static (string left, string right, SqliteStateStore store) BaselinePair(string label)
    {
        var left = NewTempDir(label + "-l"); var right = NewTempDir(label + "-r");
        var store = new SqliteStateStore(Path.Combine(NewTempDir(label + "-db"), "s.db"));
        Write(left, "chung.txt", "v1");
        new SyncSession("job", left, right, SyncMode.TwoWay, store).Run();
        return (left, right, store);
    }

    private static SyncResult RunPolicy(string left, string right, SqliteStateStore store, ConflictPolicy policy)
        => new SyncSession("job", left, right, SyncMode.TwoWay, store, policy: policy).Run().Result!;

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- P2: ConflictResolver / Job CRUD / History ---");
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // 1. NewerWins: ban Phai moi hon -> thang ca hai phia
        {
            var (left, right, store) = BaselinePair("nw");
            using var _ = store;
            Write(left, "chung.txt", "AAAA", old);
            Write(right, "chung.txt", "BBBBBB", newer);
            var r = RunPolicy(left, right, store, ConflictPolicy.NewerWins);
            Check("NewerWins: ban moi hon (Phai) thang ca 2 phia",
                Read(left, "chung.txt") == "BBBBBB" && Read(right, "chung.txt") == "BBBBBB" && r.Conflicts == 0,
                $"L={Read(left, "chung.txt")} R={Read(right, "chung.txt")} conflicts={r.Conflicts}");
            var after = new SyncSession("job", left, right, SyncMode.TwoWay, store, policy: ConflictPolicy.NewerWins).AnalyzeOnly();
            Check("NewerWins: sau khi giai quyet thi hoi tu sach (khong con hanh dong)",
                after.Actions.All(a => a.Kind == SyncActionKind.Skip));
        }

        // 2. NewerWins nhung hoa gio -> rut ve KeepBoth
        {
            var (left, right, store) = BaselinePair("tie");
            using var _ = store;
            Write(left, "chung.txt", "LLLL", old);
            Write(right, "chung.txt", "RRRRR", old);
            RunPolicy(left, right, store, ConflictPolicy.NewerWins);
            var scan = new FolderScanner().Scan(left);
            Check("NewerWins hoa gio: sinh conflict copy thay vi chon dai 1 ben",
                scan.Entries.Keys.Any(k => k.Contains("(conflict")) && Read(right, "chung.txt") == "LLLL");
        }

        // 3. LeftWins / RightWins
        {
            var (l1, r1, s1) = BaselinePair("lw");
            using var _1 = s1;
            Write(l1, "chung.txt", "TRAI-THANG", old);
            Write(r1, "chung.txt", "phai", newer);
            RunPolicy(l1, r1, s1, ConflictPolicy.LeftWins);
            Check("LeftWins: Trai thang bat ke moi/cu",
                Read(l1, "chung.txt") == "TRAI-THANG" && Read(r1, "chung.txt") == "TRAI-THANG");

            var (l2, r2, s2) = BaselinePair("rw");
            using var _2 = s2;
            Write(l2, "chung.txt", "trai", old);
            Write(r2, "chung.txt", "PHAI-THANG", newer);
            RunPolicy(l2, r2, s2, ConflictPolicy.RightWins);
            Check("RightWins: Phai thang bat ke moi/cu",
                Read(l2, "chung.txt") == "PHAI-THANG" && Read(r2, "chung.txt") == "PHAI-THANG");
        }

        // 4. KeepBoth: ban chuan = Trai, conflict copy = Phai, co o ca hai phia va hoi tu
        {
            var (left, right, store) = BaselinePair("kb");
            using var _ = store;
            Write(left, "chung.txt", "ban-chuan-trai", old);
            Write(right, "chung.txt", "ban-phai-bi-thua!!", newer);
            RunPolicy(left, right, store, ConflictPolicy.KeepBoth);
            var scanL = new FolderScanner().Scan(left);
            var scanR = new FolderScanner().Scan(right);
            var conflictKey = scanL.Entries.Keys.FirstOrDefault(k => k.Contains("(conflict"));
            Check("KeepBoth: ca 2 phia co ban chuan (Trai) + conflict copy (Phai)",
                Read(left, "chung.txt") == "ban-chuan-trai" && Read(right, "chung.txt") == "ban-chuan-trai"
                && conflictKey is not null && scanR.Entries.ContainsKey(conflictKey)
                && Read(left, conflictKey!) == "ban-phai-bi-thua!!" && Read(right, conflictKey!) == "ban-phai-bi-thua!!",
                $"keys={string.Join(",", scanL.Entries.Keys)}");
            var after = new SyncSession("job", left, right, SyncMode.TwoWay, store, policy: ConflictPolicy.KeepBoth).AnalyzeOnly();
            Check("KeepBoth: lan chay sau hoi tu sach",
                after.Actions.All(a => a.Kind == SyncActionKind.Skip));
        }

        // 5. Xoa/Sua voi policy tu dong: giu ban da sua
        {
            var (left, right, store) = BaselinePair("dm");
            using var _ = store;
            File.Delete(Path.Combine(left, "chung.txt"));
            Write(right, "chung.txt", "da-sua-o-phai-dai-hon-v1", newer);
            RunPolicy(left, right, store, ConflictPolicy.NewerWins);
            Check("Xoa/Sua: ban da sua duoc copy tro lai phia da xoa, khong mat",
                Read(left, "chung.txt") == "da-sua-o-phai-dai-hon-v1" && Read(right, "chung.txt") == "da-sua-o-phai-dai-hon-v1");
        }

        // 6. Quan ly Job: them / liet ke / xoa (kem state)
        {
            using var store = new SqliteStateStore(Path.Combine(NewTempDir("crud-db"), "s.db"));
            store.UpsertJob("a", "Job A", "/L1", "/R1", SyncMode.MirrorOneWay, ConflictPolicy.NewerWins);
            store.UpsertJob("b", "Job B", "/L2", "/R2", SyncMode.TwoWay, ConflictPolicy.KeepBoth);
            store.SaveState("a", new Dictionary<string, FileEntry> { ["x"] = new("x", 1, DateTime.UtcNow) });
            var jobs = store.ListJobs();
            Check("Job CRUD: liet ke dung 2 job voi du truong",
                jobs.Count == 2 && jobs.Single(j => j.Id == "b").Policy == ConflictPolicy.KeepBoth
                && jobs.Single(j => j.Id == "a").Mode == SyncMode.MirrorOneWay);
            store.DeleteJob("a");
            Check("Job CRUD: xoa job thi state cua job do cung bi xoa, job khac nguyen",
                store.ListJobs().Count == 1 && store.LoadState("a").Count == 0);
        }

        // 7. HistoryService: liet ke va khoi phuc sau khi Mirror xoa file
        {
            var left = NewTempDir("h-l"); var right = NewTempDir("h-r");
            using var store = new SqliteStateStore(Path.Combine(NewTempDir("h-db"), "s.db"));
            Write(left, "tai-lieu.txt", "ban goc quan trong");
            Write(left, "khac.txt", "khac");
            Write(left, "them.txt", "them");
            new SyncSession("hj", left, right, SyncMode.MirrorOneWay, store).Run();
            File.Delete(Path.Combine(left, "tai-lieu.txt"));
            new SyncSession("hj", left, right, SyncMode.MirrorOneWay, store).Run();

            var entries = HistoryService.List(right);
            var entry = entries.FirstOrDefault(e => e.RelativePath == "tai-lieu.txt");
            Check("History: liet ke duoc file da bi xoa o Phai",
                entry is not null && !Exists(right, "tai-lieu.txt"),
                $"entries={entries.Count}");
            if (entry is not null)
            {
                HistoryService.Restore(right, entry);
                Check("History: khoi phuc file ve dung vi tri va noi dung",
                    Read(right, "tai-lieu.txt") == "ban goc quan trong");
                // Khoi phuc de len ban dang co khac noi dung -> ban cu phai duoc sao vao history
                Write(right, "tai-lieu.txt", "noi dung hien tai khac");
                int before = HistoryService.List(right).Count(e => e.RelativePath == "tai-lieu.txt");
                HistoryService.Restore(right, entry);
                int afterCount = HistoryService.List(right).Count(e => e.RelativePath == "tai-lieu.txt");
                Check("History: khoi phuc ghi de thi ban hien tai cung vao history (khong mat)",
                    Read(right, "tai-lieu.txt") == "ban goc quan trong" && afterCount == before + 1,
                    $"before={before} after={afterCount}");
            }
        }

        return (_pass, _fail);
    }
}
