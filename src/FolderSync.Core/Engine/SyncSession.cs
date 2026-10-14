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

    /// <summary>Pha Analyze - khong dong vao bat ky file nao.</summary>
    public SessionOutcome AnalyzeOnly()
    {
        var leftScan = _scanner.Scan(_leftRoot, computeHashes: true);
        var rightScan = _scanner.Scan(_rightRoot, computeHashes: true);
        var state = _store.LoadState(_jobId);

        var actions = _analyzer.Analyze(_mode, state, leftScan.Entries, rightScan.Entries);
        actions = RenameDetector.Apply(actions, state, leftScan.Entries, rightScan.Entries);
        actions = ConflictResolver.Resolve(actions, _policy, state, leftScan.Entries, rightScan.Entries);

        var scanErrors = leftScan.Errors.Concat(rightScan.Errors).ToList();
        return new SessionOutcome(actions, null, scanErrors);
    }

    /// <summary>Ca 2 pha: Analyze + Execute + luu state/nhat ky.</summary>
    public SessionOutcome Run()
    {
        var started = DateTime.UtcNow;
        var preview = AnalyzeOnly();
        var stateBefore = _store.LoadState(_jobId);

        var executor = new SyncExecutor(_leftRoot, _rightRoot, _options);
        var result = executor.Execute(preview.Actions, stateBefore.Count);

        if (!result.Aborted)
        {
            var leftAfter = _scanner.Scan(_leftRoot, computeHashes: true);
            var rightAfter = _scanner.Scan(_rightRoot, computeHashes: true);
            var newState = ComputeSyncedState(leftAfter.Entries, rightAfter.Entries);
            _store.SaveState(_jobId, newState);
        }

        _store.SaveRun(_jobId, started, DateTime.UtcNow,
            result.Copied, result.Deleted, result.Moved, result.Conflicts, result.Skipped,
            result.Errors.Count + (result.Aborted ? 1 : 0));

        return new SessionOutcome(preview.Actions, result, preview.ScanErrors);
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
