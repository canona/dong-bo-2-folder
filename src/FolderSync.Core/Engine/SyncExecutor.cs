using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

public enum DeleteMode
{
    /// <summary>Mac dinh an toan: doi file vao .foldersync_history/<thoi gian>/ truoc khi xoa/ghi de.</summary>
    MoveToHistory,
    Permanent
}

public sealed record SyncOptions(
    DeleteMode DeleteMode = DeleteMode.MoveToHistory,
    /// <summary>Chot an toan: so lenh xoa vuot nguong nay (theo % so file tham chieu - xem Execute) thi huy toan bo lan chay.</summary>
    double MaxDeletePercent = 50.0,
    int MaxDeleteAbsolute = 1000,
    bool Force = false,
    /// <summary>Tab Advanced cua Job Options: copy ca thoi gian tao file (creation time) sang dich.</summary>
    bool CopyCreationTime = false,
    /// <summary>Tab Advanced: copy thuoc tinh file (tru ReadOnly - tranh file dich bi khoa ghi de o lan sau).</summary>
    bool CopyAttributes = false,
    /// <summary>Gioi han toc do chep (byte/giay) cho CA lan chay; 0 = khong gioi han. Xem BandwidthThrottle.</summary>
    long MaxBytesPerSecond = 0);

public sealed class SyncResult
{
    public int Copied { get; internal set; }
    public int Deleted { get; internal set; }
    public int Moved { get; internal set; }
    public int Conflicts { get; internal set; }
    public int Skipped { get; internal set; }
    public List<string> Errors { get; } = new();
    public bool Aborted { get; internal set; }
    public string? AbortReason { get; internal set; }
    /// <summary>Nguoi dung bam Huy giua chung (dung truoc file tiep theo; file dang chep do bi bo).</summary>
    public bool Canceled { get; internal set; }

    /// <summary>
    /// Hash da biet sau khi chep (tinh ngay trong luc chep) cua file nguon + file dich, kem size/mtime THAT
    /// sau khi ghi -> dua vao cache hash: lan quet sau va state khong phai doc lai noi dung.
    /// </summary>
    public List<(bool Left, FileEntry Entry)> KnownHashes { get; } = new();

    /// <summary>
    /// Ban chep da kiem chung (noi dung/size/mtime cua nguon LUC CHEP) theo duong dan dich, chi cho chep cung ten.
    /// Neu nguon bi sua ngay sau do thi day moi la moc "2 phia da giong" that - de lan sau nhan ra chi nguon doi
    /// (khong bao xung dot gia).
    /// </summary>
    public List<(bool DstLeft, FileEntry Entry)> VerifiedCopies { get; } = new();

    /// <summary>Duong dan (tuong doi) cua hanh dong bi loi I/O: state cu cua chung phai duoc giu, xem SyncSession.</summary>
    public List<string> FailedPaths { get; } = new();
}

public enum SyncProgressStage { Scanning, Hashing, Planned, Started, Bytes, Verifying, Completed, Failed }

/// <summary>
/// Tien trinh dong bo cho UI.
/// Scanning: dang quet (BytesDone = so file da quet, Message = "Trai/Phai: thu muc").
/// Hashing: dang so noi dung file cung size khac mtime (BytesDone/BytesTotal = byte, Message = file).
/// Planned: Plan = danh sach hanh dong se chay (Index/Action khong dung),
/// BytesTotal = tong so byte can copy cua ca Job.
/// Bytes: dang copy - BytesDone/BytesTotal cua FILE hien tai (bao toi da ~10 lan/giay).
/// Verifying: da chep xong, dang kiem chung hash file tam.
/// Started/Completed/Failed: Index la vi tri trong Plan; Destination = duong dan day du noi file duoc ghi
/// (copy/doi ten) hoac noi ban cu bi doi vao history (xoa).
/// </summary>
public sealed record SyncProgress(
    SyncProgressStage Stage,
    int Index,
    int Total,
    SyncAction? Action,
    string? Destination = null,
    string? Error = null,
    IReadOnlyList<SyncAction>? Plan = null,
    long BytesDone = 0,
    long BytesTotal = 0,
    string? Message = null,
    IReadOnlyDictionary<string, FileEntry>? ScanLeft = null,
    IReadOnlyDictionary<string, FileEntry>? ScanRight = null)
{
    public DateTime TimeLocal { get; } = DateTime.Now;
}

/// <summary>
/// Thuc thi danh sach hanh dong len 2 folder that (Ke hoach muc 2.1, 2.3f):
/// - Copy NGUYEN TU: ghi file tam ".tmp-sync" cung thu muc dich -> kiem chung size/hash -> doi ten de len.
///   Mat dien/rut USB giua chung cung khong lam hong file dich dang tot.
/// - Ghi de hoac xoa (che do MoveToHistory): ban cu duoc doi vao history truoc, khoi phuc duoc.
/// - Chot an toan: qua nhieu lenh xoa -> huy toan bo TRUOC khi dong vao bat ky file nao (tru khi Force).
/// - 1 file loi khong lam sap ca lan chay: ghi Errors va tiep tuc, state chi luu phan da hoi tu (SyncSession xu ly).
/// </summary>
public sealed class SyncExecutor
{
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly SyncOptions _options;
    // Kem mili giay de moi lan chay la 1 thu muc history rieng, khong bao gio trung lan chay truoc
    private readonly string _historyStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    /// <summary>Diem chot chich loi (xem FaultPoints) - mac dinh null, chi harness gan.</summary>
    internal Action<string, string?>? FaultHook { get; set; }

    /// <summary>
    /// Thu muc co o moi phia LUC QUET (null = Job khong dong bo thu muc rong: don thu muc cha rong nhu cu).
    /// Co gia tri thi chi don thu muc cha rong o 1 phia khi phia KIA khong co thu muc do - neu khong, xoa file
    /// cuoi cung cua 'a/' o Trai se don 'a' o Phai du 'a' van con (rong) o Trai, roi lan sau lan truyen xoa 'a'.
    /// </summary>
    internal IReadOnlySet<string>? LeftDirs { get; init; }
    internal IReadOnlySet<string>? RightDirs { get; init; }

    // Thu muc cha cua file vua xoa/doi ten: don lai lan cuoi sau ca lan chay (lan don ngay luc xoa co the
    // that bai vi Explorer/antivirus dang giu thu muc). KHONG don toan cay: ban cu xoa MOI thu muc rong o ca
    // 2 phia moi lan chay - ke ca thu muc nguoi dung vua tao o nguon (loi bao Dien Bien 11/2026).
    private readonly List<(bool Left, string Rel)> _pruneLater = new();

    public SyncExecutor(string leftRoot, string rightRoot, SyncOptions? options = null)
    {
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        _options = options ?? new SyncOptions();
    }

    private string Full(string root, string rel)
        => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string HistoryPath(string root, string rel)
        => Full(root, $".foldersync_history/{_historyStamp}/{rel}");

    /// <param name="baselineFileCount">Moc tinh % cho chot an toan: so file trong state, hoac tong so file
    /// hien co o 2 phia neu chua co state (lan chay dau).</param>
    /// <param name="progress">Bao Started/Completed/Failed cho tung hanh dong (tru Skip/Conflict).</param>
    /// <param name="pause">Tam dung giua chung (null = khong ho tro) - chi dung o diem an toan, xem PauseGate.</param>
    public SyncResult Execute(IReadOnlyList<SyncAction> actions, int baselineFileCount,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default, PauseGate? pause = null)
    {
        var result = new SyncResult();
        _pause = pause;
        // 1 bo dieu toc cho ca lan chay (gioi han tinh chung moi file, khong reset theo tung file)
        _throttle = _options.MaxBytesPerSecond > 0 ? new BandwidthThrottle(_options.MaxBytesPerSecond) : null;

        // --- Chot an toan xoa hang loat: kiem tra truoc khi thuc thi bat ky lenh nao ---
        // Xoa thu muc rong khong mat du lieu -> khong tinh vao chot (chot RootMissing van chan ca thu muc)
        var deletes = actions.Count(a => a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight && !a.IsDirectory);
        if (!_options.Force && deletes > 0)
        {
            bool overPercent = baselineFileCount > 0 && deletes * 100.0 / baselineFileCount > _options.MaxDeletePercent;
            bool overAbsolute = deletes > _options.MaxDeleteAbsolute;
            if (overPercent || overAbsolute)
            {
                result.Aborted = true;
                result.AbortReason =
                    $"Chot an toan: {deletes} lenh xoa vuot nguong ({_options.MaxDeletePercent}% cua {baselineFileCount} file tham chieu, toi da {_options.MaxDeleteAbsolute}). " +
                    "Co the do o bi rut/mat ket noi hoac state hong. Huy toan bo, chua dong vao file nao.";
                return result;
            }
        }

        actions = DeletesFirstWhenPathKindClashes(actions);
        FaultHook?.Invoke(FaultPoints.BeforeFirstAction, null);
        for (int i = 0; i < actions.Count; i++)
        {
            if (ct.IsCancellationRequested) { result.Canceled = true; break; }
            // Tam dung giua 2 hanh dong: chua dong vao file nao cua hanh dong tiep theo
            try { _pause?.WaitWhilePaused(ct); }
            catch (OperationCanceledException) { result.Canceled = true; break; }
            var action = actions[i];
            bool silent = action.Kind is SyncActionKind.Skip or SyncActionKind.Conflict;
            if (!silent) progress?.Report(new SyncProgress(SyncProgressStage.Started, i, actions.Count, action));
            string? destination = null;
            try
            {
                if (action.IsDirectory)
                {
                    destination = ExecuteDirectory(action, result);
                    if (!silent)
                        progress?.Report(new SyncProgress(SyncProgressStage.Completed, i, actions.Count, action, destination));
                    continue;
                }
                switch (action.Kind)
                {
                    case SyncActionKind.CopyLeftToRight:
                        // TargetPath (neu co) la duong dan dich khac duong dan nguon - dung cho ban "conflict copy"
                        destination = Full(_rightRoot, action.TargetPath ?? action.RelativePath);
                        RemoveEmptyTreeBlocking(destination);
                        var hashLR = AtomicCopy(Full(_leftRoot, action.RelativePath), destination,
                                   _rightRoot, action.TargetPath ?? action.RelativePath, ReporterFor(progress, i, actions), ct);
                        Remember(result, true, action.RelativePath, false, action.TargetPath ?? action.RelativePath, hashLR);
                        result.Copied++;
                        break;
                    case SyncActionKind.CopyRightToLeft:
                        destination = Full(_leftRoot, action.TargetPath ?? action.RelativePath);
                        RemoveEmptyTreeBlocking(destination);
                        var hashRL = AtomicCopy(Full(_rightRoot, action.RelativePath), destination,
                                   _leftRoot, action.TargetPath ?? action.RelativePath, ReporterFor(progress, i, actions), ct);
                        Remember(result, false, action.RelativePath, true, action.TargetPath ?? action.RelativePath, hashRL);
                        result.Copied++;
                        break;
                    case SyncActionKind.DeleteLeft:
                        destination = DeleteWithHistory(Full(_leftRoot, action.RelativePath), _leftRoot, action.RelativePath);
                        PruneEmptyParents(left: true, action.RelativePath);
                        result.Deleted++;
                        break;
                    case SyncActionKind.DeleteRight:
                        destination = DeleteWithHistory(Full(_rightRoot, action.RelativePath), _rightRoot, action.RelativePath);
                        PruneEmptyParents(left: false, action.RelativePath);
                        result.Deleted++;
                        break;
                    case SyncActionKind.MoveLeft:
                        destination = MoveWithin(left: true, action);
                        result.Moved++;
                        break;
                    case SyncActionKind.MoveRight:
                        destination = MoveWithin(left: false, action);
                        result.Moved++;
                        break;
                    case SyncActionKind.Conflict:
                        result.Conflicts++;
                        break;
                    default:
                        result.Skipped++;
                        break;
                }
                if (!silent)
                    progress?.Report(new SyncProgress(SyncProgressStage.Completed, i, actions.Count, action, destination));
            }
            catch (OperationCanceledException)
            {
                // Huy giua luc chep: AtomicCopy da xoa file tam; dung han, khong chay tiep
                result.Canceled = true;
                progress?.Report(new SyncProgress(SyncProgressStage.Failed, i, actions.Count, action, destination, "Da huy theo yeu cau"));
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = FriendlyError(ex);
                result.Errors.Add($"{action.Kind} {action.RelativePath}: {message}");
                result.FailedPaths.Add(action.RelativePath);
                if (action.TargetPath is { } failedTarget) result.FailedPaths.Add(failedTarget);
                progress?.Report(new SyncProgress(SyncProgressStage.Failed, i, actions.Count, action, destination, message));
            }
        }

        foreach (var (left, rel) in _pruneLater.ToList()) PruneEmptyParents(left, rel, retry: true);
        return result;
    }

    /// <summary>
    /// Lenh chep FILE vao duong dan dang la THU MUC khong chua file nao (chi con thu muc con rong sot lai, vd nguoi
    /// dung doi thu muc -> file o phia kia): xoa cay rong do de chep duoc. Ban &lt;= 0.3.1 nho PruneEmptyDirs don toan
    /// cay nen khong gap; bo buoc do thi lenh chep loi I/O mai (fuzz HARD-A1 2 chieu bat duoc). Con bat ky file nao
    /// (ke ca an / bi loc) hoac lien ket (junction/symlink) ben trong -> KHONG dung, de loi I/O bao ro.
    /// </summary>
    private static void RemoveEmptyTreeBlocking(string destFull)
    {
        if (!Directory.Exists(destFull)) return;
        var all = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in new DirectoryInfo(destFull).EnumerateFileSystemInfos("*", all))
            if (entry is FileInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return;
        Directory.Delete(destFull, recursive: true);
    }

    /// <summary>
    /// Tao thu muc rong o phia dich / xoa thu muc rong. Xoa KHONG de quy: luc chay ma thu muc da co file
    /// (nguoi dung vua chep vao) thi Directory.Delete bao loi -> giu nguyen, khong bao gio xoa du lieu.
    /// </summary>
    private string? ExecuteDirectory(SyncAction action, SyncResult result)
    {
        switch (action.Kind)
        {
            case SyncActionKind.CopyLeftToRight:
            case SyncActionKind.CopyRightToLeft:
            {
                var root = action.Kind == SyncActionKind.CopyLeftToRight ? _rightRoot : _leftRoot;
                // CreateDirectory tao ca chuoi cha -> USB bi rut thi se tao lai goc rong (bat bien 2): tu choi
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Thu muc goc khong ton tai: {root}");
                var full = Full(root, action.RelativePath);
                Directory.CreateDirectory(full);
                result.Copied++;
                return full;
            }
            case SyncActionKind.DeleteLeft:
            case SyncActionKind.DeleteRight:
            {
                bool left = action.Kind == SyncActionKind.DeleteLeft;
                var full = Full(left ? _leftRoot : _rightRoot, action.RelativePath);
                if (Directory.Exists(full))
                {
                    if (Directory.EnumerateFileSystemEntries(full).Any())
                        throw new IOException($"Thu muc khong con rong, giu nguyen: {action.RelativePath}");
                    Directory.Delete(full, recursive: false);
                }
                PruneEmptyParents(left, action.RelativePath);
                result.Deleted++;
                return null;
            }
            default:
                result.Skipped++;
                return null;
        }
    }

    /// <summary>
    /// Doi file &lt;-&gt; thu muc cung ten: lenh chep dich 'a' (file) phai chay SAU khi cac lenh xoa 'a/...' da don
    /// thu muc 'a', va lenh chep 'a/x' phai chay SAU khi file 'a' da bi xoa. Thu tu mac dinh theo duong dan
    /// ('a' truoc 'a/x') dung cho truong hop sau nhung SAI cho truong hop dau (chep 'a' len thu muc con day file ->
    /// loi I/O, dong bo hoi tu cham 2 lan chay). Chi keo cac lenh xoa DUNG VOI MOT LENH CHEP/DOI TEN CUNG PHIA len dau
    /// (giu nguyen thu tu tuong doi); khong cham vao thu tu cua moi lenh khac.
    /// </summary>
    internal static IReadOnlyList<SyncAction> DeletesFirstWhenPathKindClashes(IReadOnlyList<SyncAction> actions)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var createPaths = (Left: new HashSet<string>(cmp), Right: new HashSet<string>(cmp));
        var createParents = (Left: new HashSet<string>(cmp), Right: new HashSet<string>(cmp));
        foreach (var a in actions)
        {
            bool? left = a.Kind switch
            {
                SyncActionKind.CopyRightToLeft or SyncActionKind.MoveLeft => true,
                SyncActionKind.CopyLeftToRight or SyncActionKind.MoveRight => false,
                _ => null
            };
            if (left is null) continue;
            var path = a.TargetPath ?? a.RelativePath;
            var paths = left.Value ? createPaths.Left : createPaths.Right;
            var parents = left.Value ? createParents.Left : createParents.Right;
            paths.Add(path);
            for (int i = path.LastIndexOf('/'); i > 0; i = path.LastIndexOf('/', i - 1)) parents.Add(path[..i]);
        }

        bool Clashes(SyncAction a)
        {
            // Move cung "xoa" duong dan nguon (vd d09/x -> z trong khi chep file d09): phai chay truoc lenh chep
            if (a.Kind is not (SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight
                or SyncActionKind.MoveLeft or SyncActionKind.MoveRight)) return false;
            bool left = a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.MoveLeft;
            var paths = left ? createPaths.Left : createPaths.Right;
            var parents = left ? createParents.Left : createParents.Right;
            if (parents.Contains(a.RelativePath)) return true; // xoa file 'a', roi tao 'a/x'
            if (a.IsDirectory && paths.Contains(a.RelativePath)) return true; // xoa thu muc rong 'a', roi chep file 'a'
            for (int i = a.RelativePath.LastIndexOf('/'); i > 0; i = a.RelativePath.LastIndexOf('/', i - 1))
                if (paths.Contains(a.RelativePath[..i])) return true; // xoa 'a/x', roi tao file 'a'
            return false;
        }

        if (!actions.Any(Clashes)) return actions;
        return actions.Where(Clashes).Concat(actions.Where(a => !Clashes(a))).ToList();
    }

    /// <summary>
    /// Sau khi xoa file: don cac thu muc cha vua rong (toi goc, khong dong vao goc / history) de dong thoi
    /// cho trong duong dan cho lenh chep file trung ten thu muc ('a/x' xoa het -> chep file 'a').
    /// Chi xoa thu muc RONG HOAN TOAN - khong bao gio xoa du lieu. Dong bo thu muc rong bat (LeftDirs/RightDirs
    /// co gia tri) thi dung lai o thu muc ma phia kia van co: nguoi dung chi xoa file, khong xoa thu muc.
    /// </summary>
    private void PruneEmptyParents(bool left, string rel, bool retry = false)
    {
        var root = left ? _leftRoot : _rightRoot;
        var otherDirs = left ? RightDirs : LeftDirs;
        try
        {
            var rootFull = Path.GetFullPath(root);
            var dir = Path.GetDirectoryName(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            while (dir is not null && dir.Length > rootFull.Length
                   && !Path.GetFileName(dir).Equals(".foldersync_history", StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                if (otherDirs is not null
                    && otherDirs.Contains(FolderScanner.Normalize(Path.GetRelativePath(rootFull, dir)))) return;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Don dep la viec phu (thu muc dang bi Explorer/antivirus giu) - thu lai 1 lan cuoi lan chay
            if (!retry) _pruneLater.Add((left, rel));
        }
    }

    /// <summary>Ghi lai hash vua tinh cua ca file nguon va file dich (size/mtime doc lai SAU khi ghi).</summary>
    private void Remember(SyncResult result, bool srcLeft, string srcRel, bool dstLeft, string dstRel, CopiedFile copied)
    {
        try
        {
            // Nguon: dung size/mtime da kiem chung LUC CHEP, khong doc lai - nguon bi sua (cung size) sau do ma
            // ghep mtime moi voi hash noi dung cu thi cache noi dung sai va state noi 2 phia "da giong".
            result.KnownHashes.Add((srcLeft, new FileEntry(srcRel, copied.Size, copied.MtimeUtc, copied.Hash)));
            var dst = new FileInfo(Full(dstLeft ? _leftRoot : _rightRoot, dstRel));
            if (dst.Length == copied.Size)
                result.KnownHashes.Add((dstLeft, new FileEntry(dstRel, dst.Length, dst.LastWriteTimeUtc, copied.Hash)));
            if (string.Equals(srcRel, dstRel, StringComparison.OrdinalIgnoreCase))
                result.VerifiedCopies.Add((dstLeft, new FileEntry(dstRel, copied.Size, copied.MtimeUtc, copied.Hash)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* chi la cache */ }
    }

    /// <summary>Callback bao tien trinh byte cua 1 file: (stage Bytes/Verifying, da chep, tong).</summary>
    private delegate void CopyReporter(SyncProgressStage stage, long done, long total);

    private static CopyReporter? ReporterFor(IProgress<SyncProgress>? progress, int index, IReadOnlyList<SyncAction> actions)
        => progress is null ? null
           : (stage, done, total) => progress.Report(new SyncProgress(stage, index, actions.Count, actions[index],
               BytesDone: done, BytesTotal: total));

    private const int CopyBufferSize = 1024 * 1024;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private BandwidthThrottle? _throttle;
    private PauseGate? _pause;

    /// <summary>SHA-256 + size + mtime cua nguon tai thoi diem da kiem chung (khop voi noi dung da chep).</summary>
    private readonly record struct CopiedFile(string Hash, long Size, DateTime MtimeUtc);

    private CopiedFile AtomicCopy(string sourceFull, string destFull, string destRoot, string rel,
        CopyReporter? report = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
        var tmpFull = destFull + ".tmp-sync";

        // Moc cua nguon truoc khi chep - neu nguon bi sua trong luc copy thi ban chep la ban "lai"
        var srcInfo = new FileInfo(sourceFull);
        long total = srcInfo.Length;
        var mtimeBefore = srcInfo.LastWriteTimeUtc;

        // Khong du cho thi bao loi NGAY, khong ghi gi: chep do roi moi het cho vua mat thoi gian
        // vua de lai file tam chiem cho (USB FAT32 day tung bi nhu vay).
        if (FreeSpaceProbe(destFull) is { } free && free < total + DiskReserveBytes)
            throw new DiskFullException(rel, total, free);

        string hash;
        try
        {
            hash = CopyAndVerify(sourceFull, tmpFull, rel, srcInfo, total, mtimeBefore, report, ct);
            FaultHook?.Invoke(FaultPoints.AfterTempWrite, rel);
            if (File.Exists(destFull)) PreserveExisting(destFull, destRoot, rel);
            FaultHook?.Invoke(FaultPoints.BeforeRename, rel);
            File.Move(tmpFull, destFull, overwrite: true);
            FaultHook?.Invoke(FaultPoints.AfterRename, rel);
        }
        catch
        {
            // Loi o BAT KY buoc nao (het cho, rut USB, nguon doi, dich bi khoa / trung ten thu muc...)
            // -> xoa file tam do dang. Scanner bo qua *.tmp-sync nen neu de lai, no nam do chiem cho mai mai.
            try { if (File.Exists(tmpFull)) File.Delete(tmpFull); } catch (Exception) { /* o da mat - bo qua */ }
            throw;
        }
        FinishCopiedFile(destFull, srcInfo);
        return new CopiedFile(hash, total, mtimeBefore);
    }

    /// <summary>Chua lai it nhat 1 MB tren o dich (FAT32 / he thong can cho cho bang file).</summary>
    private const long DiskReserveBytes = 1024 * 1024;

    /// <summary>Do cho trong cua o chua duong dan; null = khong do duoc (UNC, o mang...) -> bo qua kiem tra.
    /// Harness thay the de gia lap het cho.</summary>
    internal static Func<string, long?> FreeSpaceProbe { get; set; } = DefaultFreeSpace;

    public static long? DefaultFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Loi het cho tren o dich - thong bao ro can bao nhieu, con bao nhieu.</summary>
    public sealed class DiskFullException(string rel, long needed, long free)
        : IOException($"Khong du cho trong o dich: can {FormatBytes(needed)}, chi con {FormatBytes(free)} ({rel})");

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes; string[] u = { "KB", "MB", "GB", "TB" }; int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return $"{v:0.##} {u[i]}";
    }

    /// <summary>Doi loi he thong thanh thong bao tieng Viet de hieu cho cac loi hay gap.</summary>
    internal static string FriendlyError(Exception ex) => ex switch
    {
        DiskFullException => ex.Message,
        IOException when (ex.HResult & 0xFFFF) is 0x70 or 0x27 => "Khong du cho trong o dich (o da day)",
        IOException when (ex.HResult & 0xFFFF) is 0x20 or 0x21 => "File dang bi chuong trinh khac mo/khoa",
        IOException when (ex.HResult & 0xFFFF) is 0x15 or 0x37 or 0x3 => "O dia/thu muc khong san sang (USB bi rut?)",
        UnauthorizedAccessException => "Khong co quyen ghi/doc (file chi doc hoac thieu quyen)",
        _ => ex.Message
    };

    private string CopyAndVerify(string sourceFull, string tmpFull, string rel, FileInfo srcInfo, long total,
        DateTime mtimeBefore, CopyReporter? report, CancellationToken ct)
    {

        // Hash nguon NGAY TRONG LUC chep (doc nguon 1 lan thay vi 2) - nhanh gap doi voi file lon
        string srcHash;
        using (var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                   System.Security.Cryptography.HashAlgorithmName.SHA256))
        // FileShare.Delete: tren Windows, thieu co nay thi nguoi dung KHONG xoa/doi ten duoc file nguon dang chep
        // (loi sharing violation) - viec xoa that bai am tham roi lan sau file bi chep nguoc / hoi sinh.
        using (var src = new FileStream(sourceFull, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, CopyBufferSize))
        using (var dst = new FileStream(tmpFull, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
        {
            // Bi gioi han toc do: khoi nho hon (~1/10 giay du lieu) de tien trinh/toc do hien muot
            var buffer = new byte[_throttle?.ChunkSize ?? CopyBufferSize];
            long done = 0;
            bool midFired = false;
            var lastReport = DateTime.UtcNow;
            report?.Invoke(SyncProgressStage.Bytes, 0, total);
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested(); // AtomicCopy bat va xoa file tam
                hasher.AppendData(buffer, 0, read);
                dst.Write(buffer, 0, read);
                if (!midFired) { midFired = true; FaultHook?.Invoke(FaultPoints.MidCopy, rel); }
                // Chi chen thoi gian cho vao vong ghi FILE TAM - thu tu copy nguyen tu khong doi;
                // huy trong luc cho -> OperationCanceledException -> AtomicCopy xoa file tam
                _throttle?.Consume(read, ct);
                // Tam dung giua 2 khoi ghi file TAM (file dich chua bi dong vao); huy luc dang dung -> xoa file tam
                _pause?.WaitWhilePaused(ct);
                done += read;
                if (report is not null && DateTime.UtcNow - lastReport >= ReportInterval)
                {
                    report(SyncProgressStage.Bytes, done, total);
                    lastReport = DateTime.UtcNow;
                }
            }
            report?.Invoke(SyncProgressStage.Bytes, done, total);
            srcHash = Convert.ToHexString(hasher.GetHashAndReset());
        }

        // Kiem chung: nguon khong doi trong luc chep + file tam dung kich thuoc va hash
        srcInfo.Refresh();
        if (srcInfo.Length != total || srcInfo.LastWriteTimeUtc != mtimeBefore)
        {
            File.Delete(tmpFull);
            throw new IOException($"File nguon bi thay doi trong luc copy {rel} - se chep lai o lan sau");
        }
        if (new FileInfo(tmpFull).Length != total)
        {
            File.Delete(tmpFull);
            throw new IOException($"Kiem chung that bai (lech kich thuoc) khi copy {rel}");
        }
        report?.Invoke(SyncProgressStage.Verifying, total, total);
        if (!FileHasher.Verify(tmpFull, srcHash))
        {
            File.Delete(tmpFull);
            throw new IOException($"Kiem chung that bai (lech hash) khi copy {rel}");
        }
        return srcHash;
    }

    private void FinishCopiedFile(string destFull, FileInfo srcInfo)
    {
        // Giu nguyen mtime cua nguon - day la tin hieu phat hien thay doi o lan quet sau
        File.SetLastWriteTimeUtc(destFull, srcInfo.LastWriteTimeUtc);
        if (_options.CopyCreationTime)
        {
            // Vai file system (USB cu, mang) khong cho dat creation time - bo qua, khong coi la loi copy
            try { File.SetCreationTimeUtc(destFull, srcInfo.CreationTimeUtc); } catch (Exception) { }
        }
        if (_options.CopyAttributes)
        {
            try
            {
                var attrs = srcInfo.Attributes & ~FileAttributes.ReadOnly & ~FileAttributes.ReparsePoint;
                File.SetAttributes(destFull, attrs);
            }
            catch (Exception) { /* thuoc tinh la phu - khong lam hong ket qua copy */ }
        }
    }

    /// <returns>Noi file bi doi vao history, hoac null neu xoa vinh vien / file khong con.</returns>
    private string? DeleteWithHistory(string full, string root, string rel)
    {
        if (!File.Exists(full)) return null;
        if (_options.DeleteMode == DeleteMode.MoveToHistory)
        {
            var hist = HistoryPath(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
            FaultHook?.Invoke(FaultPoints.BeforeHistoryMove, rel);
            File.Move(full, hist, overwrite: true);
            return hist;
        }
        File.Delete(full);
        return null;
    }

    private void PreserveExisting(string destFull, string destRoot, string rel)
    {
        if (_options.DeleteMode != DeleteMode.MoveToHistory) return;
        var hist = HistoryPath(destRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
        FaultHook?.Invoke(FaultPoints.BeforeHistoryMove, rel);
        File.Copy(destFull, hist, overwrite: true);
    }

    private string MoveWithin(bool left, SyncAction action)
    {
        var root = left ? _leftRoot : _rightRoot;
        var from = Full(root, action.RelativePath);
        var to = Full(root, action.TargetPath!);
        if (!File.Exists(from)) throw new IOException($"Khong tim thay file nguon de doi ten: {action.RelativePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        // Doi ten chi khac hoa/thuong: "to" chinh la file "from" - khong sao luu vao history
        bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(to)) PreserveExisting(to, root, action.TargetPath!);
        File.Move(from, to, overwrite: true);
        PruneEmptyParents(left, action.RelativePath);
        return to;
    }
}
