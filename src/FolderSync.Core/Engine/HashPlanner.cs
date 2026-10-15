using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

/// <summary>
/// Quyet dinh file NAO can hash - thay cho viec hash MOI file o moi lan quet (lam phan tich folder lon mat
/// hang chuc phut: do 40.020 file / 2,1 GB = 46 s chi rieng hash; khong hash = 3 s).
///
/// Nguyen tac (giong GoodSync / robocopy):
/// - Khac size        -> chac chan khac, KHONG can hash.
/// - Cung size, mtime bang nhau (cung phia: dung tung tick; khac phia: trong dung sai FAT32)
///                    -> coi la giong, KHONG hash. Gioi han: file bi sua ma giu nguyen size + mtime.
/// - Cung size, mtime khac -> MO HO (co the chi bi "touch") -> hash ca hai de so noi dung that.
/// - Nhan dien doi ten -> hash file sap bi xoa o dich + file nguon cung size (chi cac cap co the ghep).
/// Hash da biet (cache theo size+mtime, hash tinh luc chep) khong bao gio tinh lai.
/// </summary>
public static class HashPlanner
{
    public readonly record struct Need(bool Left, string RelativePath, long Size);

    public static List<Need> ForComparison(SyncMode mode,
        IReadOnlyDictionary<string, FileEntry> state,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right,
        TimeSpan tolerance)
    {
        var need = new Dictionary<(bool, string), Need>();
        void Add(bool isLeft, FileEntry? e)
        {
            if (e is not null && e.Hash is null)
                need.TryAdd((isLeft, e.RelativePath), new Need(isLeft, e.RelativePath, e.Size));
        }
        bool Near(FileEntry a, FileEntry b) => (a.MtimeUtc - b.MtimeUtc).Duration() <= tolerance;

        if (mode == SyncMode.TwoWay)
        {
            foreach (var (path, s) in state)
            {
                left.TryGetValue(path, out var l);
                right.TryGetValue(path, out var r);
                if (s.Hash is null) continue; // khong co moc noi dung de so -> analyzer dung mtime
                // State luu entry phia Trai -> so voi Trai phai dung tung tick (cung he thong file)
                if (l is not null && l.Size == s.Size && l.MtimeUtc != s.MtimeUtc) Add(true, l);
                if (r is not null && r.Size == s.Size && !Near(r, s)) Add(false, r);
            }
        }

        // 2 phia cung co file, cung size, mtime khac: 1 chieu -> co can chep lai khong;
        // 2 chieu -> cung tao / cung sua co giong nhau khong (tranh xung dot gia)
        foreach (var (path, l) in left)
        {
            if (!right.TryGetValue(path, out var r) || l.Size != r.Size || Near(l, r)) continue;
            Add(true, l);
            Add(false, r);
        }
        return need.Values.ToList();
    }

    /// <summary>
    /// Sau khi co danh sach hanh dong: hash cac file co the ghep thanh "doi ten" - file sap bi xoa o dich
    /// (A) va file nguon sap duoc chep (B) CUNG SIZE voi mot file A nao do. Khong co cap cung size thi
    /// khong hash gi (truong hop pho bien: chi them file moi).
    /// </summary>
    public static List<Need> ForRenames(IReadOnlyList<SyncAction> actions,
        IReadOnlyDictionary<string, FileEntry> state,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        var need = new Dictionary<(bool, string), Need>();
        Collect(SyncActionKind.DeleteRight, SyncActionKind.CopyLeftToRight, sourceIsLeft: true);
        Collect(SyncActionKind.DeleteLeft, SyncActionKind.CopyRightToLeft, sourceIsLeft: false);
        return need.Values.ToList();

        void Collect(SyncActionKind deleteKind, SyncActionKind copyKind, bool sourceIsLeft)
        {
            var source = sourceIsLeft ? left : right;
            var target = sourceIsLeft ? right : left;
            var deleted = actions
                .Where(a => a.Kind == deleteKind && state.ContainsKey(a.RelativePath))
                .Select(a => target.TryGetValue(a.RelativePath, out var t) ? t : null)
                .OfType<FileEntry>().ToList();
            if (deleted.Count == 0) return;
            var sizes = deleted.Select(t => t.Size).ToHashSet();
            var copied = actions
                .Where(a => a.Kind == copyKind && a.TargetPath is null)
                .Select(a => source.TryGetValue(a.RelativePath, out var e) ? e : null)
                .OfType<FileEntry>().Where(e => sizes.Contains(e.Size)).ToList();
            if (copied.Count == 0) return;
            var copiedSizes = copied.Select(e => e.Size).ToHashSet();
            foreach (var t in deleted.Where(t => copiedSizes.Contains(t.Size) && t.Hash is null))
                need.TryAdd((!sourceIsLeft, t.RelativePath), new Need(!sourceIsLeft, t.RelativePath, t.Size));
            foreach (var e in copied.Where(e => e.Hash is null))
                need.TryAdd((sourceIsLeft, e.RelativePath), new Need(sourceIsLeft, e.RelativePath, e.Size));
        }
    }
}
