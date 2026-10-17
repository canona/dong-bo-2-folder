using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.Core.Engine;

/// <summary>
/// Cache hash cua 1 Job theo tung phia (file cung size + mtime thi khong doc lai noi dung).
/// JobRunner giu 1 instance suot vong doi va SyncSession nap/ghi no vao bang hash_cache (song qua lan mo app).
/// Session tu tao (khong truyen cache) thi dung cache tam, khong ghi DB.
/// </summary>
public sealed class ScanCache
{
    public IReadOnlyDictionary<string, FileEntry>? Left { get; set; }
    public IReadOnlyDictionary<string, FileEntry>? Right { get; set; }
    // Ban da ghi DB lan cuoi - de chi ghi phan thay doi (folder 40k file: ghi lai ca bang moi lan qua ton)
    internal Dictionary<string, FileEntry>? SavedLeft, SavedRight;
    internal bool Loaded;
}

/// <param name="Left">Ket qua quet phia Trai (size/mtime/hash) - UI dung truc tiep, khong phai doc lai tung file.</param>
/// <param name="HashedFiles">So file phai doc noi dung de so sanh trong lan nay (chan doan hieu nang).</param>
public sealed record SessionOutcome(
    IReadOnlyList<SyncAction> Actions,
    SyncResult? Result,
    IReadOnlyList<string> ScanErrors,
    IReadOnlyDictionary<string, FileEntry>? Left = null,
    IReadOnlyDictionary<string, FileEntry>? Right = null,
    int HashedFiles = 0,
    long HashedBytes = 0);

/// <summary>
/// Dieu phoi 1 Job theo dung triet ly GoodSync (Ke hoach muc 1.1):
///   Pha 1 Analyze: Scan 2 phia (chi metadata) -> tai State -> hash CHI file mo ho (HashPlanner) -> so sanh 3 chieu
///                  -> hash ung vien doi ten -> nhan dien doi ten -> tra DANH SACH HANH DONG (UI goi AnalyzeOnly).
///   Pha 2 Sync:    Execute -> quet lai -> tinh state moi tu thuc te da hoi tu -> luu state + nhat ky.
/// State moi duoc tinh tu KET QUA QUET SAU KHI CHAY (chi gom file hai phia da giong nhau),
/// nen ke ca khi 1 so file loi giua chung, state khong bao gio "noi doi" rang da sync xong.
/// Neu Executor kich hoat chot an toan (Aborted) hoac nguoi dung Huy thi khong luu state.
/// </summary>
public sealed class SyncSession
{
    private readonly string _jobId;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly SyncMode _mode;
    private readonly ConflictPolicy _policy;
    private readonly SqliteStateStore _store;
    private readonly SyncOptions _options;
    private readonly FolderScanner _scanner;
    private readonly ThreeWayAnalyzer _analyzer;
    private readonly bool _detectRenames;
    private readonly bool _computeHashes;

    // Mac dinh cua engine la Manual (an toan nhat): xung dot chi duoc tu dong giai quyet
    // khi Job khai bao ro chinh sach tu dong. UI dat mac dinh NewerWins khi tao Job moi.
    // Cac tham so con lai den tu JobOptions (tab Filters/Advanced/Left/Right Side cua Job).
    /// <param name="computeHashes">true = so NOI DUNG (hash) khi size bang nhau ma mtime khac + nhan dien doi ten;
    /// false = chi so size + mtime (nhanh nhat, kem chac).</param>
    public SyncSession(string jobId, string leftRoot, string rightRoot, SyncMode mode,
        SqliteStateStore store, SyncOptions? options = null,
        ConflictPolicy policy = ConflictPolicy.Manual,
        SyncFilter? filter = null,
        bool detectRenames = true,
        bool computeHashes = true,
        bool backupPropagateDeletes = false,
        TimeSpan? timeTolerance = null,
        bool createMissingRoots = false,
        ScanCache? scanCache = null,
        bool hashAllFiles = false)
    {
        _hashAllFiles = hashAllFiles;
        _cache = scanCache ?? new ScanCache();
        _persistCache = scanCache is not null;
        _jobId = jobId;
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        _mode = mode;
        _store = store;
        _options = options ?? new SyncOptions();
        _policy = policy;
        _scanner = new FolderScanner(filter);
        _analyzer = new ThreeWayAnalyzer(timeTolerance, backupPropagateDeletes);
        _detectRenames = detectRenames;
        _computeHashes = computeHashes;
        _timeTolerance = timeTolerance ?? TimeSpan.FromSeconds(2);
        _createMissingRoots = createMissingRoots;
    }

    /// <summary>Diem chot chich loi (xem FaultPoints) - mac dinh null, chi harness gan.</summary>
    internal Action<string, string?>? FaultHook { get; set; }

    private readonly TimeSpan _timeTolerance;
    private readonly bool _hashAllFiles;
    private readonly ScanCache _cache;
    private readonly bool _persistCache;
    private readonly bool _createMissingRoots;

    private static readonly TimeSpan HashReportInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Dung sai mtime khi quyet dinh "coi la giong, khoi doc noi dung". Dung sai 2 giay chi can khi 1 phia la
    /// FAT/FAT32/exFAT (mtime lam tron 2 giay). 2 phia NTFS: mtime chinh xac 100 ns va duoc giu nguyen khi chep
    /// -> phai KHOP TUYET DOI; neu dung 2 giay cho moi o thi 2 file cung size khac noi dung tao cach nhau
    /// &lt; 2 giay bi coi la giong (do thay tren bo 40.000 file: 30.000 file khac noi dung bi bo sot).
    /// Khong xac dinh duoc loai o (o mang...) -> dung dung sai cho an toan voi lam tron.
    /// </summary>
    private TimeSpan HashTolerance()
        => IsPreciseFs(_leftRoot) && IsPreciseFs(_rightRoot) ? TimeSpan.Zero : _timeTolerance;

    private static bool IsPreciseFs(string root)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            return drive.IsReady && drive.DriveFormat is "NTFS" or "ReFS";
        }
        catch (Exception) { return false; }
    }

    // ================= Cache hash =================

    private void EnsureCacheLoaded()
    {
        if (!_persistCache || _cache.Loaded) return;
        var (left, right) = _store.LoadHashCache(_jobId);
        _cache.Left ??= left;
        _cache.Right ??= right;
        _cache.SavedLeft = new Dictionary<string, FileEntry>(left, StringComparer.OrdinalIgnoreCase);
        _cache.SavedRight = new Dictionary<string, FileEntry>(right, StringComparer.OrdinalIgnoreCase);
        _cache.Loaded = true;
    }

    /// <summary>Cap nhat cache theo ket qua quet. Phia nao goc mat (USB rut) thi GIU cache cu - cam lai khoi hash lai.</summary>
    private void UpdateCache(IReadOnlyDictionary<string, FileEntry>? left, IReadOnlyDictionary<string, FileEntry>? right)
    {
        if (left is not null) _cache.Left = left;
        if (right is not null) _cache.Right = right;
    }

    private void PersistCache()
    {
        if (!_persistCache) return;
        _cache.SavedLeft = PersistSide(true, _cache.Left, _cache.SavedLeft);
        _cache.SavedRight = PersistSide(false, _cache.Right, _cache.SavedRight);
    }

    private Dictionary<string, FileEntry>? PersistSide(bool left, IReadOnlyDictionary<string, FileEntry>? current,
        Dictionary<string, FileEntry>? saved)
    {
        if (current is null) return saved;
        saved ??= new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var upserts = current.Values.Where(e => e.Hash is not null
            && (!saved.TryGetValue(e.RelativePath, out var old) || old != e)).ToList();
        var deletes = saved.Keys.Where(k => !current.TryGetValue(k, out var c) || c.Hash is null).ToList();
        _store.SaveHashCacheDelta(_jobId, left, upserts, deletes);
        return current.Values.Where(e => e.Hash is not null)
            .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    private void MergeKnownHashes(IEnumerable<(bool Left, FileEntry Entry)> known)
    {
        var left = new Dictionary<string, FileEntry>(_cache.Left ?? new Dictionary<string, FileEntry>(), StringComparer.OrdinalIgnoreCase);
        var right = new Dictionary<string, FileEntry>(_cache.Right ?? new Dictionary<string, FileEntry>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (isLeft, entry) in known) (isLeft ? left : right)[entry.RelativePath] = entry;
        _cache.Left = left;
        _cache.Right = right;
    }

    // ================= Quet + hash =================

    private ScanResult ScanSide(bool left, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var root = left ? _leftRoot : _rightRoot;
        var side = left ? "Trai" : "Phai";
        Action<int, string>? report = progress is null ? null
            : (n, dir) => progress.Report(new SyncProgress(SyncProgressStage.Scanning, 0, 0, null,
                BytesDone: n, Message: $"{side}: {(dir.Length == 0 ? "(goc)" : dir)}"));
        // hashAllFiles: hash moi file chua co trong cache (cham) - mac dinh chi hash file mo ho (HashPlanner)
        return _scanner.Scan(root, computeHashes: _hashAllFiles, left ? _cache.Left : _cache.Right, report, ct);
    }

    /// <summary>Hash cac file trong danh sach, ghi thang vao dictionary. File khong doc duoc -> vao failed (SkipUnreadable).</summary>
    private (int Files, long Bytes) HashInto(List<HashPlanner.Need> need,
        Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right,
        List<string> failedLeft, List<string> failedRight,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (need.Count == 0) return (0, 0);
        long total = need.Sum(n => n.Size), done = 0, bytes = 0;
        int files = 0, index = 0;
        var lastReport = DateTime.MinValue;
        foreach (var n in need)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var dict = n.Left ? left : right;
            if (!dict.TryGetValue(n.RelativePath, out var entry) || entry.Hash is not null) continue;
            var full = Path.Combine(n.Left ? _leftRoot : _rightRoot, n.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var hash = FileHasher.ComputeHash(full, ct, read =>
                {
                    done += read;
                    if (progress is not null && DateTime.UtcNow - lastReport > HashReportInterval)
                    {
                        progress.Report(new SyncProgress(SyncProgressStage.Hashing, index, need.Count, null,
                            BytesDone: done, BytesTotal: total, Message: n.RelativePath));
                        lastReport = DateTime.UtcNow;
                    }
                });
                dict[n.RelativePath] = entry with { Hash = hash };
                files++;
                bytes += entry.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Khong doc duoc noi dung (bi khoa...) -> khong duoc quyet dinh tren file nay
                (n.Left ? failedLeft : failedRight).Add(n.RelativePath);
            }
        }
        return (files, bytes);
    }

    /// <summary>
    /// Tuy chon "Tao folder neu thieu" - CHI ap dung o lan chay dau (Job chua co state):
    /// 1 chieu chi tao phia dich (Phai), 2 chieu tao phia thieu. Da co state ma goc bien mat
    /// thi gan nhu chac chan la o/USB bi rut - tao folder rong luc do se bien no thanh
    /// "nguoi dung da xoa het" va qua mat chot RootMissing. Analyze khong bao gio goi ham nay.
    /// </summary>
    private IReadOnlyList<string> EnsureRootsForFirstRun()
    {
        var created = new List<string>();
        if (!_createMissingRoots || _store.LoadState(_jobId).Count > 0) return created;
        var roots = _mode == SyncMode.TwoWay ? new[] { _leftRoot, _rightRoot } : new[] { _rightRoot };
        foreach (var root in roots)
        {
            if (Directory.Exists(root)) continue;
            try
            {
                Directory.CreateDirectory(root);
                created.Add($"Da tao folder (lan chay dau): {root}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                created.Add($"Khong tao duoc folder {root}: {ex.Message}");
            }
        }
        return created;
    }

    private sealed record AnalysisContext(
        SessionOutcome Outcome,
        ScanResult Left,
        ScanResult Right,
        IReadOnlyDictionary<string, FileEntry> State,
        string? BlockReason);

    /// <summary>Pha Analyze - khong dong vao bat ky file nao. Huy -> OperationCanceledException.</summary>
    public SessionOutcome AnalyzeOnly(IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
        => Analyze(progress, ct).Outcome;

    private AnalysisContext Analyze(IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        EnsureCacheLoaded();
        var leftScan = ScanSide(left: true, progress, ct);
        var rightScan = ScanSide(left: false, progress, ct);
        FaultHook?.Invoke(FaultPoints.AfterScan, null);
        var state = _store.LoadState(_jobId);

        var left = new Dictionary<string, FileEntry>(leftScan.Entries, StringComparer.OrdinalIgnoreCase);
        var right = new Dictionary<string, FileEntry>(rightScan.Entries, StringComparer.OrdinalIgnoreCase);
        var failedLeft = leftScan.FailedPaths.ToList();
        var failedRight = rightScan.FailedPaths.ToList();
        int hashedFiles = 0;
        long hashedBytes = 0;

        if (_computeHashes)
        {
            var (f, b) = HashInto(HashPlanner.ForComparison(_mode, state, left, right, HashTolerance()),
                left, right, failedLeft, failedRight, progress, ct);
            hashedFiles += f; hashedBytes += b;
        }

        var actions = _analyzer.Analyze(_mode, state, left, right);
        if (_detectRenames)
        {
            if (_computeHashes)
            {
                var (f, b) = HashInto(HashPlanner.ForRenames(actions, state, left, right),
                    left, right, failedLeft, failedRight, progress, ct);
                hashedFiles += f; hashedBytes += b;
            }
            actions = RenameDetector.Apply(actions, state, left, right);
        }
        actions = ConflictResolver.Resolve(actions, _policy, state, left, right);
        // File/thu muc khong doc duoc luc quet trong nhu "da xoa" - khong duoc phep ra quyet dinh tren chung
        actions = SkipUnreadable(actions, failedLeft.Concat(failedRight).ToList());

        UpdateCache(leftScan.RootMissing ? null : left, rightScan.RootMissing ? null : right);
        PersistCache();

        var scanErrors = leftScan.Errors.Concat(rightScan.Errors).ToList();

        // Goc khong ton tai (USB chua cam, sai duong dan) ma lai co lenh xoa/doi ten
        // = gan nhu chac chan la su co, khong phai y nguoi dung -> chan, ke ca khi Force.
        string? blockReason = null;
        bool destructive = actions.Any(a => a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight
            or SyncActionKind.MoveLeft or SyncActionKind.MoveRight);
        if ((leftScan.RootMissing || rightScan.RootMissing) && destructive)
        {
            var missing = leftScan.RootMissing ? _leftRoot : _rightRoot;
            blockReason = $"Chot an toan: thu muc goc khong ton tai ({missing}) nhung co lenh xoa/doi ten. " +
                          "Kiem tra lai o dia/USB hoac duong dan. Huy toan bo, chua dong vao file nao.";
            scanErrors.Add(blockReason);
        }

        return new AnalysisContext(
            new SessionOutcome(actions, null, scanErrors, left, right, hashedFiles, hashedBytes),
            leftScan with { Entries = left, FailedPaths = failedLeft },
            rightScan with { Entries = right, FailedPaths = failedRight },
            state, blockReason);
    }

    /// <summary>Ca 2 pha: Analyze + Execute + luu state/nhat ky. Huy luc phan tich -> OperationCanceledException.</summary>
    public SessionOutcome Run(IProgress<SyncProgress>? progress = null, CancellationToken ct = default,
        PauseGate? pause = null)
    {
        var started = DateTime.UtcNow;
        var rootNotes = EnsureRootsForFirstRun();
        var ctx = Analyze(progress, ct);
        var preview = ctx.Outcome with { ScanErrors = rootNotes.Concat(ctx.Outcome.ScanErrors).ToList() };
        var stateBefore = ctx.State;

        SyncResult result;
        if (ctx.BlockReason is not null)
        {
            result = new SyncResult { Aborted = true, AbortReason = ctx.BlockReason };
        }
        else
        {
            // Lan chay dau (chua co state) thi lay tong so file hien co lam moc cho chot % xoa,
            // tranh truong hop Mirror lan dau voi nguon rong xoa sach dich ma khong bi chan.
            int baseline = stateBefore.Count > 0
                ? stateBefore.Count
                : ctx.Left.Entries.Count + ctx.Right.Entries.Count;
            progress?.Report(new SyncProgress(SyncProgressStage.Planned, 0, preview.Actions.Count, null,
                Plan: preview.Actions, BytesTotal: CopyBytes(preview.Actions, ctx.Left.Entries, ctx.Right.Entries),
                ScanLeft: ctx.Left.Entries, ScanRight: ctx.Right.Entries));
            var executor = new SyncExecutor(_leftRoot, _rightRoot, _options) { FaultHook = FaultHook };
            result = executor.Execute(preview.Actions, baseline, progress, ct, pause);
        }

        if (!result.Aborted && !result.Canceled)
        {
            // Hash tinh trong luc chep -> cache: quet lai + state co hash ma khong doc lai noi dung
            MergeKnownHashes(result.KnownHashes);
            var leftAfter = ScanSide(left: true, progress: null, CancellationToken.None);
            var rightAfter = ScanSide(left: false, progress: null, CancellationToken.None);
            var newState = ComputeSyncedState(leftAfter.Entries, rightAfter.Entries, _timeTolerance);
            newState = KeepVerifiedCopies(newState, result.VerifiedCopies, leftAfter.Entries, rightAfter.Entries);
            // Hanh dong loi (vd nguon bi xoa/doi ten giua luc chep) cung phai giu state cu: bo di thi lan sau
            // file con lai o phia kia nhin nhu "file moi" va bi chep nguoc ve (hoi sinh file nguoi dung vua xoa).
            var unreadable = ctx.Left.FailedPaths.Concat(ctx.Right.FailedPaths)
                .Concat(leftAfter.FailedPaths).Concat(rightAfter.FailedPaths).Concat(result.FailedPaths).ToList();
            FaultHook?.Invoke(FaultPoints.AfterExecuteBeforeSaveState, null);
            _store.SaveState(_jobId, KeepUnreadableState(newState, stateBefore, unreadable));
            UpdateCache(leftAfter.RootMissing ? null : leftAfter.Entries, rightAfter.RootMissing ? null : rightAfter.Entries);
            PersistCache();
        }

        _store.SaveRun(_jobId, started, DateTime.UtcNow,
            result.Copied, result.Deleted, result.Moved, result.Conflicts, result.Skipped,
            result.Errors.Count + (result.Aborted ? 1 : 0));

        return preview with { Result = result };
    }

    /// <summary>Tong so byte se phai chep - de UI tinh % theo dung luong, toc do va thoi gian con lai.</summary>
    internal static long CopyBytes(IReadOnlyList<SyncAction> actions,
        IReadOnlyDictionary<string, FileEntry> left, IReadOnlyDictionary<string, FileEntry> right)
    {
        long sum = 0;
        foreach (var a in actions)
        {
            var source = a.Kind switch
            {
                SyncActionKind.CopyLeftToRight => left,
                SyncActionKind.CopyRightToLeft => right,
                _ => null
            };
            if (source is not null && source.TryGetValue(a.RelativePath, out var e)) sum += e.Size;
        }
        return sum;
    }

    /// <summary>"a/b.txt" nam duoi "a" hoac bang chinh no; "." = ca thu muc goc.</summary>
    internal static bool IsUnder(string path, string failed)
        => failed == "."
           || path.Equals(failed, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(failed + "/", StringComparison.OrdinalIgnoreCase);

    private static bool Touches(SyncAction a, IReadOnlyList<string> failed)
        => failed.Any(f => IsUnder(a.RelativePath, f) || (a.TargetPath is { } t && IsUnder(t, f)));

    internal static IReadOnlyList<SyncAction> SkipUnreadable(IReadOnlyList<SyncAction> actions, IReadOnlyList<string> failed)
    {
        if (failed.Count == 0) return actions;
        return actions
            .Select(a => a.Kind != SyncActionKind.Skip && Touches(a, failed)
                ? new SyncAction(SyncActionKind.Skip, a.RelativePath,
                    $"Bo qua: khong doc duoc luc quet (du dinh: {a.Kind})", a.TargetPath)
                : a)
            .ToList();
    }

    /// <summary>
    /// File vua chep xong ma nguon bi sua NGAY SAU DO (khong con giong dich nen ComputeSyncedState bo qua): ghi
    /// ban da kiem chung luc chep lam moc state, de lan sau thay dung "chi nguon doi, dich nguyen" thay vi
    /// "2 phia deu khac, khong co state" = xung dot gia. Chi ap dung khi DICH van dung la ban vua chep.
    /// </summary>
    internal IReadOnlyDictionary<string, FileEntry> KeepVerifiedCopies(
        IReadOnlyDictionary<string, FileEntry> synced,
        IReadOnlyList<(bool DstLeft, FileEntry Entry)> copies,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        if (copies.Count == 0) return synced;
        var result = new Dictionary<string, FileEntry>(synced, StringComparer.OrdinalIgnoreCase);
        foreach (var (dstLeft, copied) in copies)
        {
            if (result.ContainsKey(copied.RelativePath)) continue;
            var dst = dstLeft ? left : right;
            if (!dst.TryGetValue(copied.RelativePath, out var d) || d.Size != copied.Size) continue;
            bool same = d.Hash is not null && copied.Hash is not null
                ? string.Equals(d.Hash, copied.Hash, StringComparison.OrdinalIgnoreCase)
                : (d.MtimeUtc - copied.MtimeUtc).Duration() <= _timeTolerance;
            if (same) result[copied.RelativePath] = copied;
        }
        return result;
    }

    /// <summary>
    /// Giu nguyen entry state cu cho cac duong dan khong doc duoc - neu bo di, lan sau engine
    /// se tuong la "file moi" va hoi sinh file da bi xoa o phia kia.
    /// </summary>
    internal static IReadOnlyDictionary<string, FileEntry> KeepUnreadableState(
        IReadOnlyDictionary<string, FileEntry> synced,
        IReadOnlyDictionary<string, FileEntry> stateBefore,
        IReadOnlyList<string> failed)
    {
        if (failed.Count == 0) return synced;
        var result = new Dictionary<string, FileEntry>(synced, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in stateBefore)
            if (!result.ContainsKey(path) && failed.Any(f => IsUnder(path, f)))
                result[path] = entry;
        return result;
    }

    /// <summary>
    /// State moi = cac file ca hai phia cung co va da giong nhau: size + hash, hoac size + mtime
    /// trong dung sai khi khong co hash (Job tat CompareChecksums).
    /// </summary>
    internal static IReadOnlyDictionary<string, FileEntry> ComputeSyncedState(
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right,
        TimeSpan timeTolerance)
    {
        var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, l) in left)
        {
            if (!right.TryGetValue(path, out var r)) continue;
            if (l.Size != r.Size) continue;
            if (l.Hash is not null && r.Hash is not null)
            {
                if (!string.Equals(l.Hash, r.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if ((l.MtimeUtc - r.MtimeUtc).Duration() > timeTolerance) continue;
            // 2 phia da coi la giong -> hash biet o 1 phia dung cho ca 2: state luon co moc noi dung de so lan sau
            result[path] = l.Hash is null && r.Hash is not null ? l with { Hash = r.Hash } : l;
        }
        return result;
    }
}
