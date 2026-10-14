using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

/// <summary>
/// Trai tim cua he thong: so sanh 3 chieu State cu - Trai nay - Phai nay.
/// Chi sinh DANH SACH HANH DONG (pha Analyze), khong thuc thi copy/xoa.
/// Che do Job chi thay doi bo quy tac quyet dinh, khong thay doi loi so sanh.
/// Dung sai thoi gian mac dinh 2 giay (FAT32/USB) - xem Ke hoach muc 2.1, 7.1.
/// </summary>
public sealed class ThreeWayAnalyzer
{
    private readonly TimeSpan _timeTolerance;

    public ThreeWayAnalyzer(TimeSpan? timeTolerance = null)
        => _timeTolerance = timeTolerance ?? TimeSpan.FromSeconds(2);

    private bool Changed(FileEntry current, FileEntry baseline)
    {
        if (current.Size != baseline.Size) return true;
        if (current.Hash is not null && baseline.Hash is not null)
            return !string.Equals(current.Hash, baseline.Hash, StringComparison.OrdinalIgnoreCase);
        return (current.MtimeUtc - baseline.MtimeUtc).Duration() > _timeTolerance;
    }

    private static bool SameContent(FileEntry a, FileEntry b)
        => a.Size == b.Size && a.Hash is not null && b.Hash is not null
           && string.Equals(a.Hash, b.Hash, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SyncAction> Analyze(
        SyncMode mode,
        IReadOnlyDictionary<string, FileEntry> state,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        var actions = new List<SyncAction>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in state.Keys) paths.Add(p);
        foreach (var p in left.Keys) paths.Add(p);
        foreach (var p in right.Keys) paths.Add(p);

        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            state.TryGetValue(path, out var s);
            left.TryGetValue(path, out var l);
            right.TryGetValue(path, out var r);

            actions.Add(mode switch
            {
                SyncMode.TwoWay => DecideTwoWay(path, s, l, r),
                SyncMode.MirrorOneWay => DecideMirror(path, s, l, r),
                _ => DecideBackup(path, s, l, r)
            });
        }
        return actions;
    }

    private SyncAction DecideTwoWay(string path, FileEntry? s, FileEntry? l, FileEntry? r)
    {
        if (s is null)
        {
            if (l is not null && r is null) return new(SyncActionKind.CopyLeftToRight, path, "Moi tao o Trai");
            if (l is null && r is not null) return new(SyncActionKind.CopyRightToLeft, path, "Moi tao o Phai");
            if (l is not null && r is not null)
                return SameContent(l, r)
                    ? new(SyncActionKind.Skip, path, "Hai phia giong het, chi cap nhat state")
                    : new(SyncActionKind.Conflict, path, "Cung tao moi, khac noi dung");
            return new(SyncActionKind.Skip, path, "Khong con o ca hai phia");
        }

        bool lChanged = l is not null && Changed(l, s);
        bool rChanged = r is not null && Changed(r, s);
        bool lDeleted = l is null;
        bool rDeleted = r is null;

        if (!lDeleted && !rDeleted)
        {
            if (!lChanged && !rChanged) return new(SyncActionKind.Skip, path, "Khong doi");
            if (lChanged && !rChanged) return new(SyncActionKind.CopyLeftToRight, path, "Sua o Trai");
            if (!lChanged && rChanged) return new(SyncActionKind.CopyRightToLeft, path, "Sua o Phai");
            return SameContent(l!, r!)
                ? new(SyncActionKind.Skip, path, "Hai phia sua giong nhau")
                : new(SyncActionKind.Conflict, path, "Cung sua o hai phia");
        }
        if (lDeleted && !rDeleted)
        {
            if (rChanged) return new(SyncActionKind.Conflict, path, "Xoa o Trai, sua o Phai - giu ban da sua");
            return new(SyncActionKind.DeleteRight, path, "Da xoa o Trai");
        }
        if (!lDeleted && rDeleted)
        {
            if (lChanged) return new(SyncActionKind.Conflict, path, "Sua o Trai, xoa o Phai - giu ban da sua");
            return new(SyncActionKind.DeleteLeft, path, "Da xoa o Phai");
        }
        return new(SyncActionKind.Skip, path, "Da xoa o ca hai phia");
    }

    private SyncAction DecideMirror(string path, FileEntry? s, FileEntry? l, FileEntry? r)
    {
        if (l is not null)
        {
            if (r is null) return new(SyncActionKind.CopyLeftToRight, path, "Thieu o Phai");
            return ChangedAgainst(l, r) ? new(SyncActionKind.CopyLeftToRight, path, "Khac voi nguon")
                                        : new(SyncActionKind.Skip, path, "Da giong nguon");
        }
        if (r is not null) return new(SyncActionKind.DeleteRight, path, "Mirror: Phai thua so voi Trai");
        return new(SyncActionKind.Skip, path, "Khong con o ca hai phia");
    }

    private SyncAction DecideBackup(string path, FileEntry? s, FileEntry? l, FileEntry? r)
    {
        if (l is null) return new(SyncActionKind.Skip, path, "Backup: nguon da xoa, giu nguyen o dich");
        if (r is null) return new(SyncActionKind.CopyLeftToRight, path, "Thieu o dich");
        return ChangedAgainst(l, r) ? new(SyncActionKind.CopyLeftToRight, path, "Nguon moi hon/khac dich")
                                    : new(SyncActionKind.Skip, path, "Da co o dich");
    }

    // So sanh truc tiep 2 ban hien tai (dung cho 1 chieu, khong can state)
    private bool ChangedAgainst(FileEntry source, FileEntry target)
    {
        if (source.Size != target.Size) return true;
        if (source.Hash is not null && target.Hash is not null)
            return !string.Equals(source.Hash, target.Hash, StringComparison.OrdinalIgnoreCase);
        return (source.MtimeUtc - target.MtimeUtc).Duration() > _timeTolerance;
    }
}
