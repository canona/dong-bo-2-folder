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

    private static string? Read(string root, string rel)
        => File.Exists(Full(root, rel)) ? File.ReadAllText(Full(root, rel)) : null;

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

            // Doi mtime (giu nguyen noi dung + size) -> engine PHAI doc noi dung de biet co doi that khong;
            // file dang bi khoa -> khong doc duoc -> khong duoc coi la da xoa / da doi.
            // (Scanner chi doc metadata nen file khoa ma khong can so noi dung thi van duoc liet ke binh thuong.)
            File.SetLastWriteTimeUtc(Full(left, "khoa.txt"), DateTime.UtcNow.AddMinutes(5));
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
                events.FirstOrDefault(e => e.Stage is not (SyncProgressStage.Scanning or SyncProgressStage.Hashing))?.Stage == SyncProgressStage.Planned
                && events.Any(e => e.Stage == SyncProgressStage.Scanning)
                && events.Count(e => e.Stage == SyncProgressStage.Started) == 2 && completed.Count == 2,
                string.Join(",", events.Select(e => e.Stage)));
            Check("Tien trinh: Destination la file that o Phai",
                completed.All(e => e.Destination is { } d && File.Exists(d) && d.StartsWith(right)),
                string.Join(";", completed.Select(e => e.Destination)));
        }

        // 10. Tien trinh theo byte: Planned co tong byte, moi file co Bytes cuoi = 100% roi Verifying
        {
            var left = NewTempDir("p10-l"); var right = NewTempDir("p10-r");
            File.WriteAllBytes(Full(left, "lon.bin"), new byte[3 * 1024 * 1024 + 123]);
            Write(left, "nho.txt", "abc");
            using var store = NewStore("p10");
            var events = new List<SyncProgress>();
            new SyncSession("j", left, right, SyncMode.MirrorOneWay, store).Run(new CollectProgress(events));
            long expected = 3 * 1024 * 1024 + 123 + 3;
            var bytes = events.Where(e => e.Stage == SyncProgressStage.Bytes && e.Action!.RelativePath == "lon.bin").ToList();
            Check("Tien trinh byte: Planned.BytesTotal = tong dung luong can chep",
                events.First(e => e.Stage == SyncProgressStage.Planned).BytesTotal == expected,
                $"{events.First(e => e.Stage == SyncProgressStage.Planned).BytesTotal} != {expected}");
            Check("Tien trinh byte: file lon co Bytes tang dan, ket thuc o 100%",
                bytes.Count >= 2 && bytes[0].BytesDone == 0 && bytes[^1].BytesDone == bytes[^1].BytesTotal
                && bytes.Zip(bytes.Skip(1)).All(x => x.First.BytesDone <= x.Second.BytesDone),
                string.Join(",", bytes.Select(b => b.BytesDone)));
            Check("Tien trinh byte: co buoc Verifying truoc Completed, noi dung dich dung",
                events.Any(e => e.Stage == SyncProgressStage.Verifying)
                && new FileInfo(Full(right, "lon.bin")).Length == 3 * 1024 * 1024 + 123);
        }

        // 11. Doi folder cua Job: state cu phai bi xoa, neu khong 2 chieu se lan truyen xoa sai
        {
            var left = NewTempDir("j11-l"); var right = NewTempDir("j11-r"); var newLeft = NewTempDir("j11-l2");
            for (int i = 0; i < 10; i++) Write(left, $"f{i}.txt", "d" + i);
            for (int i = 0; i < 6; i++) Write(newLeft, $"f{i}.txt", "d" + i); // folder moi chi co 6/10 file
            using var store = NewStore("j11");
            store.UpsertJob("j", "Job", left, right, SyncMode.TwoWay, ConflictPolicy.NewerWins);
            new SyncSession("j", left, right, SyncMode.TwoWay, store).Run();

            bool sameRootsKeep = !store.UpsertJob("j", "Doi ten", left.ToUpperInvariant() + "\\", right,
                SyncMode.TwoWay, ConflictPolicy.NewerWins) && store.LoadState("j").Count == 10;
            Check("Doi folder: chi doi ten / hoa-thuong / dau '\\' cuoi -> giu state", sameRootsKeep);

            bool reset = store.UpsertJob("j", "Job", newLeft, right, SyncMode.TwoWay, ConflictPolicy.NewerWins);
            Check("Doi folder Trai -> UpsertJob bao true va xoa state cu", reset && store.LoadState("j").Count == 0);

            var o = new SyncSession("j", newLeft, right, SyncMode.TwoWay, store).Run();
            Check("Doi folder Trai (6/10 file) -> KHONG xoa 4 file con lai ben Phai",
                o.Result!.Deleted == 0 && Directory.GetFiles(right).Length == 10,
                $"deleted={o.Result.Deleted} con={Directory.GetFiles(right).Length}");
        }

        // 12. Het cho tren o dich: bao loi ngay, khong ghi gi, khong de lai file tam
        {
            var left = NewTempDir("d12-l"); var right = NewTempDir("d12-r");
            File.WriteAllBytes(Full(left, "lon.bin"), new byte[3 * 1024 * 1024]);
            var oldProbe = SyncExecutor.FreeSpaceProbe;
            SyncExecutor.FreeSpaceProbe = _ => 10; // gia lap o dich chi con 10 byte
            try
            {
                var r = new SyncExecutor(left, right).Execute(
                    new List<SyncAction> { new(SyncActionKind.CopyLeftToRight, "lon.bin", "test") }, 1);
                Check("Het cho: bao loi ro rang 'Khong du cho', khong chep",
                    r.Copied == 0 && r.Errors.Count == 1 && r.Errors[0].Contains("Khong du cho"), string.Join(";", r.Errors));
                Check("Het cho: khong tao file dich lan file tam .tmp-sync",
                    Directory.GetFiles(right).Length == 0, string.Join(",", Directory.GetFiles(right)));
            }
            finally { SyncExecutor.FreeSpaceProbe = oldProbe; }
        }

        // 13. Loi GIUA LUC dang ghi (gia lap rut USB / het cho) -> file tam do dang phai bi xoa
        {
            var left = NewTempDir("d13-l"); var right = NewTempDir("d13-r");
            File.WriteAllBytes(Full(left, "lon.bin"), new byte[3 * 1024 * 1024]);
            var r = new SyncExecutor(left, right).Execute(
                new List<SyncAction> { new(SyncActionKind.CopyLeftToRight, "lon.bin", "test") }, 1,
                new ThrowWhileWriting());
            Check("Loi giua chung: ghi nhan loi va KHONG de lai file tam .tmp-sync",
                r.Errors.Count == 1 && Directory.GetFiles(right).Length == 0,
                $"errors={r.Errors.Count} files={string.Join(",", Directory.GetFiles(right).Select(Path.GetFileName))}");
        }

        // 14. Loi o buoc doi ten file tam -> file dich (dich co THU MUC trung ten) -> khong de lai file tam
        {
            var left = NewTempDir("d14-l"); var right = NewTempDir("d14-r");
            Write(left, "bao-cao.txt", "noi dung");
            Directory.CreateDirectory(Full(right, "bao-cao.txt")); // trung ten voi file can chep
            var r = new SyncExecutor(left, right).Execute(
                new List<SyncAction> { new(SyncActionKind.CopyLeftToRight, "bao-cao.txt", "test") }, 1);
            Check("Loi khi doi ten sang dich: ghi nhan loi, KHONG de lai file tam .tmp-sync",
                r.Errors.Count == 1 && Directory.GetFiles(right).Length == 0,
                $"errors={r.Errors.Count} files={string.Join(",", Directory.GetFiles(right).Select(Path.GetFileName))}");
        }

        // 15. Khoi phuc NHIEU file tu history + trang thai "da khoi phuc"
        {
            var left = NewTempDir("h15-l"); var right = NewTempDir("h15-r");
            Write(left, "a.txt", "a"); Write(left, "b.txt", "b"); Write(left, "c.txt", "v1");
            for (int i = 0; i < 4; i++) Write(left, $"dem{i}.txt", "d" + i); // de 2 lenh xoa khong cham chot 50%
            using var store = NewStore("h15");
            var s = new SyncSession("j", left, right, SyncMode.MirrorOneWay, store);
            s.Run();
            Write(left, "c.txt", "v2-dai-hon"); s.Run();      // history: c v1
            Write(left, "c.txt", "v3-dai-hon-nua"); s.Run();  // history: c v2
            File.Delete(Full(left, "a.txt")); File.Delete(Full(left, "b.txt")); s.Run(); // history: a, b

            var all = HistoryService.List(right);
            var st = all.ToDictionary(e => e.RelativePath + "@" + e.Size, e => HistoryService.GetStatus(right, e));
            Check("History: file da xoa -> 'file goc da bi xoa'; ban cu cua file dang co -> 'hien tai khac'",
                all.Count == 4
                && all.Where(e => e.RelativePath is "a.txt" or "b.txt").All(e => HistoryService.GetStatus(right, e) == HistoryStatus.OriginalMissing)
                && all.Where(e => e.RelativePath == "c.txt").All(e => HistoryService.GetStatus(right, e) == HistoryStatus.CurrentDiffers),
                string.Join(", ", st.Select(kv => kv.Key + "=" + kv.Value)));

            var res = HistoryService.RestoreMany(right, all);
            Check("Khoi phuc nhieu file: a, b va ban MOI NHAT cua c duoc khoi phuc; ban cu hon cua c bi bo qua",
                res.Count(r => r.Restored) == 3 && Read(right, "a.txt") == "a" && Read(right, "b.txt") == "b"
                && Read(right, "c.txt") == "v2-dai-hon" && res.Any(r => !r.Restored && r.Message.Contains("moi hon")),
                string.Join("; ", res.Select(r => $"{r.Entry.RelativePath}:{r.Message}")));

            var a = HistoryService.List(right).First(e => e.RelativePath == "a.txt");
            Check("Sau khi khoi phuc: trang thai 'giong file hien tai'", HistoryService.GetStatus(right, a) == HistoryStatus.SameAsCurrent);
            var again = HistoryService.RestoreMany(right, new[] { a });
            Check("Khoi phuc lai file da khoi phuc -> bo qua, khong tao them ban sao",
                !again[0].Restored && again[0].Message.Contains("da giong")
                && HistoryService.List(right).Count(e => e.RelativePath == "a.txt") == 1);
            Check("Khoi phuc: khong de lai file tam .tmp-sync",
                !Directory.GetFiles(right, "*.tmp-sync", SearchOption.AllDirectories).Any());
        }

        return (_pass, _fail);
    }

    // Nem IOException khi da ghi xong du lieu vao file tam (truoc khi kiem chung) - nhu o bi rut giua chung
    private sealed class ThrowWhileWriting : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value)
        {
            if (value.Stage == SyncProgressStage.Bytes && value.BytesDone > 0 && value.BytesDone == value.BytesTotal)
                throw new IOException("Gia lap: thiet bi bi rut giua chung");
        }
    }

    // Progress<T> day ve SynchronizationContext (bat dong bo) - test can ban dong bo
    private sealed class CollectProgress(List<SyncProgress> sink) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => sink.Add(value);
    }
}
