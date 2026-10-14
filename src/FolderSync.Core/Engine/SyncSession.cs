using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.Core.Engine;

public sealed record SessionOutcome(
    IReadOnlyList<SyncAction> Actions,
    SyncResult? Result,
    IReadOnlyList<string> ScanErrors);

/// <summary>
/// Dieu phoi 1 Job theo dung triet ly GoodSync (Ke hoach muc 1.1):
///   Pha 1 Analyze: Scan 2 phia -> tai State -> so sanh 3 chieu -> nhan dien doi ten
///                  -> tra DANH SACH HANH DONG de xem truoc (UI goi AnalyzeOnly).
///   Pha 2 Sync:    Execute -> quet lai -> tinh state moi tu thuc te da hoi tu -> luu state + nhat ky.
/// State moi duoc tinh tu KET QUA QUET SAU KHI CHAY (chi gom file hai phia da giong nhau),
/// nen ke ca khi 1 so file loi giua chung, state khong bao gio "noi doi" rang da sync xong.
/// Neu Executor kich hoat chot an toan (Aborted) thi khong luu state.
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
    public SyncSession(string jobId, string leftRoot, string rightRoot, SyncMode mode,
        SqliteStateStore store, SyncOptions? options = null,
        ConflictPolicy policy = ConflictPolicy.Manual,
        SyncFilter? filter = null,
        bool detectRenames = true,
        bool computeHashes = true,
        bool backupPropagateDeletes = false,
        TimeSpan? timeTolerance = null,
        bool createMissingRoots = false)
    {
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

    private readonly TimeSpan _timeTolerance;
    private readonly bool _createMissingRoots;

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

    /// <summary>Pha Analyze - khong dong vao bat ky file nao.</summary>
    public SessionOutcome AnalyzeOnly() => Analyze().Outcome;

    private AnalysisContext Analyze()
    {
        var leftScan = _scanner.Scan(_leftRoot, computeHashes: _computeHashes);
        var rightScan = _scanner.Scan(_rightRoot, computeHashes: _computeHashes);
        var state = _store.LoadState(_jobId);

        var actions = _analyzer.Analyze(_mode, state, leftScan.Entries, rightScan.Entries);
        if (_detectRenames)
            actions = RenameDetector.Apply(actions, state, leftScan.Entries, rightScan.Entries);
        actions = ConflictResolver.Resolve(actions, _policy, state, leftScan.Entries, rightScan.Entries);
        // File/thu muc khong doc duoc luc quet trong nhu "da xoa" - khong duoc phep ra quyet dinh tren chung
        actions = SkipUnreadable(actions, leftScan.FailedPaths.Concat(rightScan.FailedPaths).ToList());

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

        return new AnalysisContext(new SessionOutcome(actions, null, scanErrors),
            leftScan, rightScan, state, blockReason);
    }

    /// <summary>Ca 2 pha: Analyze + Execute + luu state/nhat ky.</summary>
    public SessionOutcome Run(IProgress<SyncProgress>? progress = null)
    {
        var started = DateTime.UtcNow;
        var rootNotes = EnsureRootsForFirstRun();
        var ctx = Analyze();
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
                Plan: preview.Actions));
            var executor = new SyncExecutor(_leftRoot, _rightRoot, _options);
            result = executor.Execute(preview.Actions, baseline, progress);
        }

        if (!result.Aborted)
        {
            var leftAfter = _scanner.Scan(_leftRoot, computeHashes: _computeHashes);
            var rightAfter = _scanner.Scan(_rightRoot, computeHashes: _computeHashes);
            var newState = ComputeSyncedState(leftAfter.Entries, rightAfter.Entries, _timeTolerance);
            var unreadable = ctx.Left.FailedPaths.Concat(ctx.Right.FailedPaths)
                .Concat(leftAfter.FailedPaths).Concat(rightAfter.FailedPaths).ToList();
            _store.SaveState(_jobId, KeepUnreadableState(newState, stateBefore, unreadable));
        }

        _store.SaveRun(_jobId, started, DateTime.UtcNow,
            result.Copied, result.Deleted, result.Moved, result.Conflicts, result.Skipped,
            result.Errors.Count + (result.Aborted ? 1 : 0));

        return new SessionOutcome(preview.Actions, result, preview.ScanErrors);
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
            result[path] = l;
        }
        return result;
    }
}
