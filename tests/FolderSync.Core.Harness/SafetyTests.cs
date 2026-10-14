using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Kiem chung cac ban sua an toan: goc khong ton tai, file khong doc duoc, chot % lan dau,
// doi ten hoa/thuong, RenameDetector o Mirror, SyncFilter duong dan rong.
public static class SafetyTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] SAFE {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-safe-{label}-{Guid.NewGuid():N}");
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

    private static int CountFiles(string root) => new FolderScanner().Scan(root).Entries.Count;

    // Ten file thuc te tren dia (dung hoa/thuong), khong qua so sanh khong phan biet
    private static string ActualName(string root, string rel)
        => Path.GetFileName(Directory.GetFiles(Path.GetDirectoryName(Full(root, rel))!)
            .Single(f => string.Equals(Path.GetFileName(f), Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase)));

    private static SqliteStateStore NewStore(string label) => new(Path.Combine(NewTempDir(label + "-db"), "s.db"));

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- SAFE: Chot an toan khi quet loi / goc mat / doi ten hoa-thuong ---");

        // 1. Mirror lan dau, Trai khong ton tai -> chan, Phai nguyen ven
        {
            var right = NewTempDir("m1-r");
            for (int i = 0; i < 20; i++) Write(right, $"f{i}.txt", "d" + i);
            using var store = NewStore("m1");
            var o = new SyncSession("j", Path.Combine(right, "..", "khong-ton-tai-" + Guid.NewGuid().ToString("N")),
                right, SyncMode.MirrorOneWay, store).Run();
            Check("Mirror, Trai khong ton tai, chua co state -> huy, Phai con du 20 file",
                o.Result!.Aborted && o.Result.Deleted == 0 && CountFiles(right) == 20,
                $"aborted={o.Result.Aborted} deleted={o.Result.Deleted} con={CountFiles(right)}");
            Check("Mirror, Trai khong ton tai -> ly do huy neu ro thu muc goc",
                o.Result.AbortReason?.Contains("khong ton tai") == true, o.Result.AbortReason ?? "");
        }

        // 2. Chot an toan khong bi vuot qua bang Force khi goc mat
        {
            var right = NewTempDir("m2-r");
            Write(right, "a.txt", "a");
            using var store = NewStore("m2");
            var o = new SyncSession("j", Path.Combine(right, "..", "mat-" + Guid.NewGuid().ToString("N")),
                right, SyncMode.MirrorOneWay, store, new SyncOptions(Force: true)).Run();
            Check("Goc mat + Force -> van huy", o.Result!.Aborted && CountFiles(right) == 1);
        }

        // 3. Mirror lan dau, Trai ton tai nhung rong -> chot % (moc = tong file 2 phia) chan
        {
            var left = NewTempDir("m3-l"); var right = NewTempDir("m3-r");
            for (int i = 0; i < 10; i++) Write(right, $"f{i}.txt", "d" + i);
            using var store = NewStore("m3");
            var o = new SyncSession("j", left, right, SyncMode.MirrorOneWay, store).Run();
            Check("Mirror lan dau, nguon rong -> chot % huy, dich con 10 file",
                o.Result!.Aborted && CountFiles(right) == 10, $"aborted={o.Result.Aborted} con={CountFiles(right)}");
        }

        // 4. Mirror lan dau, it file thua o dich -> van chay binh thuong (khong chan nham)
        {
            var left = NewTempDir("m4-l"); var right = NewTempDir("m4-r");
            for (int i = 0; i < 10; i++) Write(left, $"f{i}.txt", "d" + i);
            Write(right, "thua1.txt", "x"); Write(right, "thua2.txt", "y");
            using var store = NewStore("m4");
            var o = new SyncSession("j", left, right, SyncMode.MirrorOneWay, store).Run();
            Check("Mirror lan dau, 2 file thua/12 -> khong bi chan, dich giong nguon",
                !o.Result!.Aborted && o.Result.Deleted == 2 && CountFiles(right) == 10,
                $"aborted={o.Result.Aborted} deleted={o.Result.Deleted} con={CountFiles(right)}");
        }

        // 5. File bi khoa (khong doc duoc) o Trai -> khong bi coi la "da xoa", state duoc giu
        {
            var left = NewTempDir("l5-l"); var right = NewTempDir("l5-r");
            Write(left, "khoa.txt", "noi dung");
            for (int i = 0; i < 4; i++) Write(left, $"dem{i}.txt", "d" + i);
            using var store = NewStore("l5");
            var session = new SyncSession("j", left, right, SyncMode.TwoWay, store);
            session.Run();

            SessionOutcome o;
            using (new FileStream(Full(left, "khoa.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                o = session.Run();
            Check("File khoa o Trai -> khong xoa ban o Phai",
                File.Exists(Full(right, "khoa.txt")) && o.Result!.Deleted == 0,
                $"deleted={o.Result?.Deleted} ton tai={File.Exists(Full(right, "khoa.txt"))}");
            Check("File khoa o Trai -> hanh dong la Skip co ly do loi quet",
                o.Actions.Any(a => a.RelativePath == "khoa.txt" && a.Kind == SyncActionKind.Skip && a.Reason.Contains("khong doc duoc")));
            Check("File khoa -> state cu cua file van duoc giu", store.LoadState("j").ContainsKey("khoa.txt"));

            // Nho state duoc giu: xoa o Phai sau khi mo khoa thi lan truyen xoa sang Trai (khong hoi sinh)
            File.Delete(Full(right, "khoa.txt"));
            session.Run();
            Check("Sau khi mo khoa, xoa o Phai duoc lan truyen dung (khong hoi sinh file)",
                !File.Exists(Full(left, "khoa.txt")) && !File.Exists(Full(right, "khoa.txt")));
        }

        // 6. Doi ten chi khac hoa/thuong
        {
            var left = NewTempDir("c6-l"); var right = NewTempDir("c6-r");
            Write(left, "thu muc/baocao.txt", "bc"); Write(left, "ghichu.txt", "gc");
            using var store = NewStore("c6");
            var session = new SyncSession("j", left, right, SyncMode.TwoWay, store);
            session.Run();

            File.Move(Full(left, "thu muc/baocao.txt"), Full(left, "thu muc/BaoCao.txt"));
            var r1 = session.Run();
            Check("2 chieu: doi ten hoa/thuong o Trai -> Phai doi theo",
                ActualName(right, "thu muc/baocao.txt") == "BaoCao.txt" && r1.Result!.Moved == 1,
                $"ten Phai={ActualName(right, "thu muc/baocao.txt")} moved={r1.Result?.Moved}");
            Check("2 chieu: doi ten hoa/thuong khong de lai ban sao trong history",
                HistoryService.List(right).Count == 0);

            File.Move(Full(right, "ghichu.txt"), Full(right, "GhiChu.txt"));
            session.Run();
            Check("2 chieu: doi ten hoa/thuong o Phai -> Trai doi theo", ActualName(left, "ghichu.txt") == "GhiChu.txt");

            var again = session.AnalyzeOnly().Actions.Count(a => a.Kind != SyncActionKind.Skip);
            Check("2 chieu: sau doi ten hoa/thuong thi hoi tu sach", again == 0, $"con {again} hanh dong");
        }
        {
            var left = NewTempDir("c7-l"); var right = NewTempDir("c7-r");
            Write(left, "a.TXT", "a"); Write(right, "a.txt", "a");
            using var store = NewStore("c7");
            new SyncSession("j", left, right, SyncMode.MirrorOneWay, store).Run();
            Check("Mirror: ten o dich theo dung hoa/thuong cua nguon", ActualName(right, "a.txt") == "a.TXT");
        }

        // 7. Mirror: doi ten o Trai nhung ban o Phai da bi sua -> khong Move noi dung sai
        {
            var left = NewTempDir("r8-l"); var right = NewTempDir("r8-r");
            Write(left, "a.txt", "goc");
            for (int i = 0; i < 4; i++) Write(left, $"dem{i}.txt", "d" + i); // de 1 lenh xoa khong cham chot 50%
            using var store = NewStore("r8");
            var session = new SyncSession("j", left, right, SyncMode.MirrorOneWay, store);
            session.Run();
            File.Move(Full(left, "a.txt"), Full(left, "b.txt"));
            Write(right, "a.txt", "da sua o dich");
            var o = session.Run();
            Check("Mirror: Phai da sua -> khong sinh MoveRight, b.txt dung noi dung nguon",
                !o.Actions.Any(a => a.Kind == SyncActionKind.MoveRight)
                && File.ReadAllText(Full(right, "b.txt")) == "goc" && !File.Exists(Full(right, "a.txt")),
                string.Join(",", o.Actions.Select(a => a.Kind)));
        }

        // 8. SyncFilter voi duong dan rong khong nem ngoai le
        {
            bool ok;
            try { ok = !new SyncFilter().IsIncluded(""); } catch { ok = false; }
            Check("SyncFilter.IsIncluded(\"\") tra false, khong nem", ok);
        }

        // 9. Bao tien trinh: Planned truoc, moi copy co Started + Completed kem duong dan dich that
        {
            var left = NewTempDir("p9-l"); var right = NewTempDir("p9-r");
            Write(left, "a.txt", "a"); Write(left, "thu muc/b.txt", "b");
            using var store = NewStore("p9");
            var events = new List<SyncProgress>();
            new SyncSession("j", left, right, SyncMode.TwoWay, store).Run(new CollectProgress(events));
            var completed = events.Where(e => e.Stage == SyncProgressStage.Completed).ToList();
            Check("Tien trinh: Planned dau tien, 2 Started + 2 Completed",
                events.FirstOrDefault()?.Stage == SyncProgressStage.Planned
                && events.Count(e => e.Stage == SyncProgressStage.Started) == 2 && completed.Count == 2,
                string.Join(",", events.Select(e => e.Stage)));
            Check("Tien trinh: Destination la file that o Phai",
                completed.All(e => e.Destination is { } d && File.Exists(d) && d.StartsWith(right)),
                string.Join(";", completed.Select(e => e.Destination)));
        }

        return (_pass, _fail);
    }

    // Progress<T> day ve SynchronizationContext (bat dong bo) - test can ban dong bo
    private sealed class CollectProgress(List<SyncProgress> sink) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => sink.Add(value);
    }
}
