using FolderSync.Core.Engine;
using FolderSync.Core.Models;

// Harness kiem chung bang quyet dinh 3 chieu (Ke hoach muc 2.3a) - chay bang: dotnet run
var analyzer = new ThreeWayAnalyzer();
var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
int pass = 0, fail = 0;

FileEntry E(string p, long size = 100, int addSec = 0, string? hash = null)
    => new(p, size, t0.AddSeconds(addSec), hash);
Dictionary<string, FileEntry> D(params FileEntry[] es)
    => es.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

void Check(string name, SyncMode mode,
    Dictionary<string, FileEntry> s, Dictionary<string, FileEntry> l, Dictionary<string, FileEntry> r,
    SyncActionKind expected)
{
    var got = analyzer.Analyze(mode, s, l, r).Single().Kind;
    bool ok = got == expected;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}: expected={expected} got={got}");
    if (ok) pass++; else fail++;
}

// --- TwoWay ---
Check("2way: khong doi", SyncMode.TwoWay, D(E("a.txt")), D(E("a.txt")), D(E("a.txt")), SyncActionKind.Skip);
Check("2way: sua o Trai", SyncMode.TwoWay, D(E("a.txt")), D(E("a.txt", 200)), D(E("a.txt")), SyncActionKind.CopyLeftToRight);
Check("2way: sua o Phai", SyncMode.TwoWay, D(E("a.txt")), D(E("a.txt")), D(E("a.txt", 200)), SyncActionKind.CopyRightToLeft);
Check("2way: cung sua khac nhau", SyncMode.TwoWay, D(E("a.txt")), D(E("a.txt", 200)), D(E("a.txt", 300)), SyncActionKind.Conflict);
Check("2way: xoa Trai, Phai khong doi", SyncMode.TwoWay, D(E("a.txt")), D(), D(E("a.txt")), SyncActionKind.DeleteRight);
Check("2way: xoa Trai, sua Phai", SyncMode.TwoWay, D(E("a.txt")), D(), D(E("a.txt", 200)), SyncActionKind.Conflict);
Check("2way: moi tao o Trai", SyncMode.TwoWay, D(), D(E("a.txt")), D(), SyncActionKind.CopyLeftToRight);
Check("2way: moi tao o Phai", SyncMode.TwoWay, D(), D(), D(E("a.txt")), SyncActionKind.CopyRightToLeft);
Check("2way: cung tao, giong hash", SyncMode.TwoWay, D(), D(E("a.txt", 100, 0, "H1")), D(E("a.txt", 100, 0, "H1")), SyncActionKind.Skip);
Check("2way: cung tao, khac hash", SyncMode.TwoWay, D(), D(E("a.txt", 100, 0, "H1")), D(E("a.txt", 100, 0, "H2")), SyncActionKind.Conflict);
Check("2way: lech gio 1s (trong dung sai FAT32)", SyncMode.TwoWay, D(E("a.txt")), D(E("a.txt", 100, 1)), D(E("a.txt")), SyncActionKind.Skip);

// --- Mirror ---
Check("mirror: thieu o Phai", SyncMode.MirrorOneWay, D(), D(E("a.txt")), D(), SyncActionKind.CopyLeftToRight);
Check("mirror: thua o Phai -> xoa", SyncMode.MirrorOneWay, D(), D(), D(E("a.txt")), SyncActionKind.DeleteRight);
Check("mirror: da giong", SyncMode.MirrorOneWay, D(E("a.txt")), D(E("a.txt")), D(E("a.txt")), SyncActionKind.Skip);

// --- Backup ---
Check("backup: nguon xoa, dich giu nguyen", SyncMode.BackupOneWay, D(E("a.txt")), D(), D(E("a.txt")), SyncActionKind.Skip);
Check("backup: nguon moi -> copy", SyncMode.BackupOneWay, D(), D(E("a.txt")), D(), SyncActionKind.CopyLeftToRight);

// --- Filter ---
var filter = new SyncFilter();
void FCheck(string name, string path, bool expected)
{
    bool ok = filter.IsIncluded(path) == expected;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] filter {name}");
    if (ok) pass++; else fail++;
}
FCheck("file thuong", "docs/a.txt", true);
FCheck("file tam Office", "docs/~$a.docx", false);
FCheck("trong .git", ".git/config", false);
FCheck("Thumbs.db", "anh/Thumbs.db", false);

var p1 = P1Tests.Run();
pass += p1.pass; fail += p1.fail;
var p2 = P2Tests.Run();
pass += p2.pass; fail += p2.fail;
var runTests = JobRunnerTests.Run();
pass += runTests.pass; fail += runTests.fail;
var safe = SafetyTests.Run();
pass += safe.pass; fail += safe.fail;
var opt = OptionsTests.Run();
pass += opt.pass; fail += opt.fail;
var perf = PerfTests.Run();
pass += perf.pass; fail += perf.fail;
var bw = BandwidthTests.Run();
pass += bw.pass; fail += bw.fail;
var hard = JobRunnerHardTests.Run();
pass += hard.pass; fail += hard.fail;
var hardExclude = ExcludeHardTests.Run();
pass += hardExclude.pass; fail += hardExclude.fail;

// Don thu muc tam cua moi test (tien to "foldersync-") - khong don thi moi lan chay de lai hang tram MB
// trong %TEMP% va tung lam DAY O C: (SQLite bao "database or disk is full").
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // nha file .db dang bi pool giu
int cleaned = 0, locked = 0;
foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "foldersync-*"))
{
    try { Directory.Delete(dir, recursive: true); cleaned++; }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { locked++; }
}
Console.WriteLine($"\nDa don {cleaned} thu muc tam" + (locked > 0 ? $" ({locked} dang bi khoa, de lan sau)" : ""));

Console.WriteLine($"\nTONG: {pass} PASS, {fail} FAIL");
return fail == 0 ? 0 : 1;
