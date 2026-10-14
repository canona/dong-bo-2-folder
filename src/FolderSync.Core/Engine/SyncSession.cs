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
    private readonly FolderScanner _scanner = new();
    private readonly ThreeWayAnalyzer _analyzer = new();

    // Mac dinh cua engine la Manual (an toan nhat): xung dot chi duoc tu dong giai quyet
    // khi Job khai bao ro chinh sach tu dong. UI dat mac dinh NewerWins khi tao Job moi.
    public SyncSession(string jobId, string leftRoot, string rightRoot, SyncMode mode,
        SqliteStateStore store, SyncOptions? options = null,
        ConflictPolicy policy = ConflictPolicy.Manual)
    {
        _jobId = jobId;
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        _mode = mode;
        _store = store;
        _options = options ?? new SyncOptions();
        _policy = policy;
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
        var leftScan = _scanner.Scan(_leftRoot, computeHashes: true);
        var rightScan = _scanner.Scan(_rightRoot, computeHashes: true);
        var state = _store.LoadState(_jobId);

        var actions = _analyzer.Analyze(_mode, state, leftScan.Entries, rightScan.Entries);
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
        var ctx = Analyze();
        var preview = ctx.Outcome;
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
            var leftAfter = _scanner.Scan(_leftRoot, computeHashes: true);
            var rightAfter = _scanner.Scan(_rightRoot, computeHashes: true);
            var newState = ComputeSyncedState(leftAfter.Entries, rightAfter.Entries);
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

    /// <summary>State moi = cac file ca hai phia cung co va da giong nhau (size + hash).</summary>
    internal static IReadOnlyDictionary<string, FileEntry> ComputeSyncedState(
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, l) in left)
        {
            if (!right.TryGetValue(path, out var r)) continue;
            if (l.Size != r.Size) continue;
            if (l.Hash is not null && r.Hash is not null
                && !string.Equals(l.Hash, r.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            result[path] = l;
        }
        return result;
    }
}
