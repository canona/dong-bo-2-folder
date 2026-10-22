using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

// Bo test kho giai doan 1c: HARD-L1 - file da duoc quan ly (co trong state) roi bi them mau Exclude:
// Mirror KHONG duoc xoa no o dich. Exclude = "khong quan ly nua", khong phai "da xoa o nguon".
public static class ExcludeHardTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] HARD {name}{(ok ? "" : " :: " + detail)}");
        if (ok) _pass++; else _fail++;
    }

    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"foldersync-hard-{label}-{Guid.NewGuid():N}");
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

    private static bool IsDelete(SyncActionKind k)
        => k is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight;

    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n--- HARD 1c: L1 file dang quan ly bi Exclude -> Mirror khong xoa o dich ---");
        L1ExcludeManagedFile();
        return (_pass, _fail);
    }

    private static void L1ExcludeManagedFile()
    {
        var root = NewTempDir("l1");
        var left = Path.Combine(root, "L"); var right = Path.Combine(root, "R");
        // File dem de 1 lenh xoa khong bi chot 50% huy nham (neu co xoa thi test phai thay lenh Delete, khong phai Aborted)
        for (int i = 0; i < 8; i++) Write(left, $"dem{i}.txt", "dem" + i);
        Write(left, "quan-ly.dat", "v1");
        using var store = new SqliteStateStore(Path.Combine(root, "s.db"));

        SyncSession Session(string exclude) => new("l1", left, right, SyncMode.MirrorOneWay, store,
            new SyncOptions(), ConflictPolicy.NewerWins,
            filter: new JobOptions { ExcludePatterns = exclude }.BuildFilter());

        // a) dong bo Mirror: file co o dich + trong state
        var first = Session("").Run();
        Check("L1a Mirror lan 1: quan-ly.dat co o dich, dung noi dung, nam trong state",
            Read(right, "quan-ly.dat") == "v1" && store.LoadState("l1").ContainsKey("quan-ly.dat"),
            $"copied={first.Result?.Copied} dich={Read(right, "quan-ly.dat")}");

        // b) them Exclude trung file -> Analyze + Mirror
        var preview = Session("quan-ly.dat").AnalyzeOnly();
        var del = preview.Actions.Where(a => IsDelete(a.Kind)).Select(a => $"{a.Kind}:{a.RelativePath}").ToList();
        Check("L1b Exclude: Analyze KHONG de xuat Delete nao cho quan-ly.dat",
            !preview.Actions.Any(a => a.RelativePath == "quan-ly.dat" && IsDelete(a.Kind)), string.Join(",", del));
        Check("L1b Exclude: Analyze khong ghi gi (dich van nguyen)", Read(right, "quan-ly.dat") == "v1");

        var second = Session("quan-ly.dat").Run();
        Check("L1b Exclude: sau Mirror, quan-ly.dat VAN con nguyen o dich",
            Read(right, "quan-ly.dat") == "v1",
            $"dich={Read(right, "quan-ly.dat") ?? "(MAT)"} deleted={second.Result?.Deleted} aborted={second.Result?.Aborted}");
        Check("L1b Exclude: khong co hanh dong Delete nao duoc thuc thi",
            second.Result is not null && second.Result.Deleted == 0,
            $"deleted={second.Result?.Deleted}");
        Check("L1b Exclude: khong de lai file nao trong history cho quan-ly.dat",
            !Directory.Exists(Path.Combine(right, ".foldersync_history"))
            || !Directory.GetFiles(Path.Combine(right, ".foldersync_history"), "quan-ly.dat", SearchOption.AllDirectories).Any());

        // c) Exclude con hieu luc: sua o nguon -> khong lan sang dich
        Write(left, "quan-ly.dat", "v2-sua-o-nguon");
        var third = Session("quan-ly.dat").Run();
        Check("L1c Exclude con hieu luc: sua nguon KHONG lan sang dich (dich giu v1)",
            Read(right, "quan-ly.dat") == "v1" && third.Result?.Deleted == 0,
            $"dich={Read(right, "quan-ly.dat") ?? "(MAT)"}");
        Check("L1c Exclude con hieu luc: nguon van la ban sua", Read(left, "quan-ly.dat") == "v2-sua-o-nguon");

        // d) bo Exclude: file duoc quan ly tro lai binh thuong
        var fourth = Session("").Run();
        Check("L1d Bo Exclude: lan chay dau tien khong mat file o dich / nguon",
            Read(left, "quan-ly.dat") is not null && Read(right, "quan-ly.dat") is not null,
            $"left={Read(left, "quan-ly.dat")} right={Read(right, "quan-ly.dat")} aborted={fourth.Result?.Aborted}");
        Check("L1d Bo Exclude: dich duoc cap nhat theo nguon (v2-sua-o-nguon)",
            Read(right, "quan-ly.dat") == "v2-sua-o-nguon",
            $"dich={Read(right, "quan-ly.dat")} actions={string.Join(",", fourth.Actions.Where(a => a.RelativePath == "quan-ly.dat").Select(a => a.Kind))}");

        Write(left, "quan-ly.dat", "v3");
        Session("").Run();
        Check("L1d Bo Exclude: sua nguon tiep -> dich cap nhat theo (v3), file duoc quan ly binh thuong",
            Read(right, "quan-ly.dat") == "v3", $"dich={Read(right, "quan-ly.dat")}");
        Check("L1d Bo Exclude: quet lai doc lap Trai = Phai (size + hash), khong sot .tmp-sync",
            Directory.GetFiles(root, "*.tmp-sync", SearchOption.AllDirectories).Length == 0
            && Directory.GetFiles(left).Select(f => Path.GetFileName(f)).Order()
                .SequenceEqual(Directory.GetFiles(right).Select(f => Path.GetFileName(f)).Order())
            && Directory.GetFiles(left).All(f => File.ReadAllText(f) == Read(right, Path.GetFileName(f))));
    }
}
