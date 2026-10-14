using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung P1 end-to-end tren thu muc tam that: Scanner, StateStore, Rename, Executor, Session.
public static class P1Tests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] P1 {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-p1-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string? Read(string root, string rel)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private static bool Exists(string root, string rel)
        => File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- P1: Scanner / StateStore / Rename / Executor / Session ---");
        var scanner = new FolderScanner();

        // 1. Scanner: cay thu muc + bo loc
        {
            var root = NewTempDir("scan");
            Write(root, "a.txt", "A");
            Write(root, "sub/b.txt", "B");
            Write(root, "sub/~$tam.docx", "tmp");
            Write(root, ".git/config", "git");
            var scan = scanner.Scan(root);
            Check("scanner liet ke dung file, ap bo loc",
                scan.Entries.Count == 2 && scan.Entries.ContainsKey("sub/b.txt") && scan.Errors.Count == 0,
                $"count={scan.Entries.Count} errors={string.Join(";", scan.Errors)}");
        }

        // 2. Scanner: khong di theo symlink
        {
            var root = NewTempDir("link");
            Write(root, "real.txt", "R");
            bool linkOk = true;
            try { File.CreateSymbolicLink(Path.Combine(root, "link.txt"), Path.Combine(root, "real.txt")); }
            catch { linkOk = false; }
            var scan = scanner.Scan(root);
            Check("scanner bo qua symlink",
                !linkOk || (scan.SkippedLinks >= 1 && !scan.Entries.ContainsKey("link.txt")),
                $"skipped={scan.SkippedLinks}");
        }

        // 3. StateStore: ghi/doc vong tron + nhat ky chay
        {
            var db = Path.Combine(NewTempDir("db"), "state.db");
            using var store = new SqliteStateStore(db);
            store.UpsertJob("j1", "Job 1", "/L", "/R", SyncMode.TwoWay, ConflictPolicy.NewerWins);
            var entries = new Dictionary<string, FileEntry>
            {
                ["x.txt"] = new("x.txt", 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "ABC"),
                ["sub/y.txt"] = new("sub/y.txt", 20, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), null),
            };
            store.SaveState("j1", entries);
            var loaded = store.LoadState("j1");
            store.SaveRun("j1", DateTime.UtcNow, DateTime.UtcNow, 1, 2, 3, 0, 4, 0);
            Check("sqlite luu/tai state dung, ghi duoc sync_runs",
                loaded.Count == 2 && loaded["x.txt"].Hash == "ABC" && loaded["sub/y.txt"].Size == 20
                && store.CountRuns("j1") == 1);
            // Ghi de state: snapshot cu bi thay the hoan toan
            store.SaveState("j1", new Dictionary<string, FileEntry> { ["z.txt"] = new("z.txt", 1, DateTime.UtcNow) });
            Check("sqlite save state thay the snapshot cu", store.LoadState("j1").Count == 1);
        }

        // 4+5. Mirror: lan dau, lan hai khong lam gi, xoa co history
        {
            var left = NewTempDir("m-left"); var right = NewTempDir("m-right");
            var db = Path.Combine(NewTempDir("m-db"), "s.db");
            using var store = new SqliteStateStore(db);
            Write(left, "bao-cao.docx", "noi dung bao cao");
            Write(left, "anh/anh-1.jpg", "JPEGDATA");
            Write(left, "Tài liệu tiếng Việt.txt", "tieng Viet co dau");

            var s1 = new SyncSession("mirror", left, right, SyncMode.MirrorOneWay, store).Run();
            Check("mirror lan 1: copy du 3 file (ke ca ten tieng Viet)",
                s1.Result is { Copied: 3 } && Read(right, "Tài liệu tiếng Việt.txt") == "tieng Viet co dau"
                && Read(right, "anh/anh-1.jpg") == "JPEGDATA",
                $"copied={s1.Result?.Copied} errors={string.Join(";", s1.Result?.Errors ?? [])}");

            var s2 = new SyncSession("mirror", left, right, SyncMode.MirrorOneWay, store).Run();
            Check("mirror lan 2: khong copy lai (state dung)",
                s2.Result is { Copied: 0, Deleted: 0 }, $"copied={s2.Result?.Copied} deleted={s2.Result?.Deleted}");

            File.Delete(Path.Combine(left, "bao-cao.docx"));
            var s3 = new SyncSession("mirror", left, right, SyncMode.MirrorOneWay, store).Run();
            bool inHistory = Directory.Exists(right) &&
                Directory.GetFiles(right, "bao-cao.docx", SearchOption.AllDirectories)
                    .Any(p => p.Contains(".foldersync_history"));
            Check("mirror xoa: lan truyen xoa + file vao history, khoi phuc duoc",
                s3.Result is { Deleted: 1 } && !Exists(right, "bao-cao.docx") && inHistory);
        }

        // 6+7. TwoWay: hoi tu 2 chieu + xung dot khong ghi de am tham
        {
            var left = NewTempDir("t-left"); var right = NewTempDir("t-right");
            var db = Path.Combine(NewTempDir("t-db"), "s.db");
            using var store = new SqliteStateStore(db);
            Write(left, "chung.txt", "v1");
            new SyncSession("tw", left, right, SyncMode.TwoWay, store).Run();

            Write(left, "chi-trai.txt", "L");
            Write(right, "chi-phai.txt", "R");
            new SyncSession("tw", left, right, SyncMode.TwoWay, store).Run();
            Check("2 chieu: file moi o moi phia deu hoi tu sang phia kia",
                Read(right, "chi-trai.txt") == "L" && Read(left, "chi-phai.txt") == "R");

            Write(left, "chung.txt", "ban-sua-ben-trai-dai-hon");
            Write(right, "chung.txt", "ban sua ben phai!");
            var conflictRun = new SyncSession("tw", left, right, SyncMode.TwoWay, store).Run();
            Check("2 chieu: cung sua 1 file -> xung dot, khong ben nao bi ghi de",
                conflictRun.Result is { Conflicts: >= 1 }
                && Read(left, "chung.txt") == "ban-sua-ben-trai-dai-hon"
                && Read(right, "chung.txt") == "ban sua ben phai!",
                $"conflicts={conflictRun.Result?.Conflicts}");
        }

        // 8. Doi ten: nhan dien Move thay vi Xoa+Copy
        {
            var left = NewTempDir("r-left"); var right = NewTempDir("r-right");
            var db = Path.Combine(NewTempDir("r-db"), "s.db");
            using var store = new SqliteStateStore(db);
            Write(left, "ten-cu.txt", "noi dung khong doi");
            new SyncSession("rn", left, right, SyncMode.TwoWay, store).Run();
            File.Move(Path.Combine(left, "ten-cu.txt"), Path.Combine(left, "ten-moi.txt"));

            var session = new SyncSession("rn", left, right, SyncMode.TwoWay, store);
            var preview = session.AnalyzeOnly();
            var move = preview.Actions.SingleOrDefault(a => a.Kind == SyncActionKind.MoveRight);
            Check("doi ten o Trai -> Analyze sinh MoveRight (khong Xoa+Copy)",
                move is { TargetPath: "ten-moi.txt" }, string.Join(";", preview.Actions.Select(a => a.Kind)));
            var run = session.Run();
            Check("doi ten: thuc thi Move o Phai, noi dung nguyen ven",
                run.Result is { Moved: >= 1 } && Read(right, "ten-moi.txt") == "noi dung khong doi"
                && !Exists(right, "ten-cu.txt"));
        }

        // 9. Chot an toan: xoa 80% -> huy toan bo, dich nguyen ven
        {
            var left = NewTempDir("a-left"); var right = NewTempDir("a-right");
            var db = Path.Combine(NewTempDir("a-db"), "s.db");
            using var store = new SqliteStateStore(db);
            for (int i = 0; i < 10; i++) Write(left, $"f{i}.txt", $"data {i}");
            new SyncSession("ab", left, right, SyncMode.MirrorOneWay, store).Run();
            for (int i = 0; i < 8; i++) File.Delete(Path.Combine(left, $"f{i}.txt"));
            var run = new SyncSession("ab", left, right, SyncMode.MirrorOneWay, store).Run();
            int stillThere = Enumerable.Range(0, 10).Count(i => Exists(right, $"f{i}.txt"));
            Check("chot an toan: dinh xoa 8/10 file -> huy, Phai con nguyen 10 file",
                run.Result is { Aborted: true } && stillThere == 10,
                $"aborted={run.Result?.Aborted} con={stillThere}");
        }

        // 10. Backup: khong lan truyen xoa
        {
            var left = NewTempDir("b-left"); var right = NewTempDir("b-right");
            var db = Path.Combine(NewTempDir("b-db"), "s.db");
            using var store = new SqliteStateStore(db);
            Write(left, "giu-lai.txt", "quan trong");
            new SyncSession("bk", left, right, SyncMode.BackupOneWay, store).Run();
            File.Delete(Path.Combine(left, "giu-lai.txt"));
            Write(left, "moi.txt", "moi");
            new SyncSession("bk", left, right, SyncMode.BackupOneWay, store).Run();
            Check("backup: nguon xoa thi dich van giu, file moi van duoc copy",
                Exists(right, "giu-lai.txt") && Exists(right, "moi.txt"));
        }

        // 11. Copy nguyen tu: noi dung + mtime khop sau sync
        {
            var left = NewTempDir("i-left"); var right = NewTempDir("i-right");
            var db = Path.Combine(NewTempDir("i-db"), "s.db");
            using var store = new SqliteStateStore(db);
            var big = new string('x', 1_000_000) + Guid.NewGuid();
            Write(left, "big.bin", big);
            var mtime = new DateTime(2025, 5, 4, 3, 2, 1, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(left, "big.bin"), mtime);
            new SyncSession("in", left, right, SyncMode.MirrorOneWay, store).Run();
            var rInfo = new FileInfo(Path.Combine(right, "big.bin"));
            Check("copy nguyen tu: file 1MB dung noi dung, giu nguyen mtime",
                Read(right, "big.bin") == big && (rInfo.LastWriteTimeUtc - mtime).Duration() < TimeSpan.FromSeconds(1),
                $"mtime lech={(rInfo.LastWriteTimeUtc - mtime).Duration()}");
            Check("copy nguyen tu: khong con file tam .tmp-sync",
                !Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Any());
        }

        return (_pass, _fail);
    }
}
