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
    private readonly bool _backupPropagateDeletes;

    public ThreeWayAnalyzer(TimeSpan? timeTolerance = null, bool backupPropagateDeletes = false)
    {
        _timeTolerance = timeTolerance ?? TimeSpan.FromSeconds(2);
        _backupPropagateDeletes = backupPropagateDeletes;
    }

    private bool Changed(FileEntry current, FileEntry baseline)
    {
        if (current.Size != baseline.Size) return true;
        if (current.Hash is not null && baseline.Hash is not null)
            return !string.Equals(current.Hash, baseline.Hash, StringComparison.OrdinalIgnoreCase);
        return (current.MtimeUtc - baseline.MtimeUtc).Duration() > _timeTolerance;
    }

    // Co hash 2 phia: so hash. Thieu hash (Job tat CompareChecksums): cung size + mtime trong dung sai
    // moi coi la giong - nhat quan voi Changed(); neu khong 2 file giong het se thanh xung dot gia.
    private bool SameContent(FileEntry a, FileEntry b)
        => !Changed(a, b);

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

            var action = mode switch
            {
                SyncMode.TwoWay => DecideTwoWay(path, s, l, r),
                SyncMode.MirrorOneWay => DecideMirror(path, s, l, r),
                _ => DecideBackup(path, s, l, r)
            };
            actions.Add(action.Kind == SyncActionKind.Skip ? DecideCaseRename(mode, action, s, l, r) : action);
        }
        return actions;
    }

    /// <summary>
    /// Windows khong phan biet hoa/thuong nen "baocao.txt" -> "BaoCao.txt" lot qua so sanh theo khoa.
    /// Chi dong bo phan TEN FILE (thu muc giu nguyen) de lenh Move luon hoi tu, khong lap moi lan chay.
    /// 1 chieu: Phai theo Trai. 2 chieu: phia nao khac ten trong state la phia vua doi ten.
    /// </summary>
    private static SyncAction DecideCaseRename(SyncMode mode, SyncAction skip, FileEntry? s, FileEntry? l, FileEntry? r)
    {
        if (l is null || r is null) return skip;
        var lName = FileName(l.RelativePath);
        var rName = FileName(r.RelativePath);
        if (string.Equals(lName, rName, StringComparison.Ordinal)) return skip;

        if (mode != SyncMode.TwoWay)
            return new(SyncActionKind.MoveRight, r.RelativePath, "Doi ten hoa/thuong theo nguon", WithName(r.RelativePath, lName));

        if (s is null) return skip; // Khong co moc de biet phia nao doi ten
        var sName = FileName(s.RelativePath);
        if (string.Equals(rName, sName, StringComparison.Ordinal))
            return new(SyncActionKind.MoveRight, r.RelativePath, "Doi ten hoa/thuong o Trai", WithName(r.RelativePath, lName));
        if (string.Equals(lName, sName, StringComparison.Ordinal))
            return new(SyncActionKind.MoveLeft, l.RelativePath, "Doi ten hoa/thuong o Phai", WithName(l.RelativePath, rName));
        return skip; // Ca hai phia deu doi ten khac nhau - de nguyen
    }

    private static string FileName(string rel) => rel[(rel.LastIndexOf('/') + 1)..];

    private static string WithName(string rel, string name) => rel[..(rel.LastIndexOf('/') + 1)] + name;

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
        if (l is null)
        {
            // Chi lan truyen xoa khi Job bat PropagateDeletes VA file tung co trong state
            // (tuc la truoc day da tung dong bo) - file la xuat hien san o dich thi khong dung vao.
            if (_backupPropagateDeletes && s is not null && r is not null)
                return new(SyncActionKind.DeleteRight, path, "Backup co lan truyen xoa: nguon da xoa");
            return new(SyncActionKind.Skip, path, "Backup: nguon da xoa, giu nguyen o dich");
        }
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
