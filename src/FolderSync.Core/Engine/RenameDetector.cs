using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

/// <summary>
/// Nhan dien doi ten / di chuyen (Ke hoach muc 2.3c):
/// Sau Analyze, cap [Delete o phia X tai A] + [Copy vao phia X tai B] ma noi dung B
/// giong file o state tai A (cung size + cung hash) duoc gop thanh 1 lenh Move o phia X,
/// thay vi xoa roi copy lai tu dau (ton bang thong va mat lich su tren USB cham).
/// Chi ghep khi khop 1-1 duy nhat; mo ho (nhieu ung vien giong nhau) thi giu nguyen cap goc.
/// </summary>
public static class RenameDetector
{
    public static IReadOnlyList<SyncAction> Apply(
        IReadOnlyList<SyncAction> actions,
        IReadOnlyDictionary<string, FileEntry> state,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        var result = actions.ToList();

        // File bi doi ten o Trai: DeleteRight(A) + CopyLeftToRight(B), left[B] == state[A]
        TryPair(result,
            deleteKind: SyncActionKind.DeleteRight, copyKind: SyncActionKind.CopyLeftToRight,
            moveKind: SyncActionKind.MoveRight, sourceScan: left, state);

        // File bi doi ten o Phai: DeleteLeft(A) + CopyRightToLeft(B), right[B] == state[A]
        TryPair(result,
            deleteKind: SyncActionKind.DeleteLeft, copyKind: SyncActionKind.CopyRightToLeft,
            moveKind: SyncActionKind.MoveLeft, sourceScan: right, state);

        return result;
    }

    private static void TryPair(
        List<SyncAction> actions,
        SyncActionKind deleteKind, SyncActionKind copyKind, SyncActionKind moveKind,
        IReadOnlyDictionary<string, FileEntry> sourceScan,
        IReadOnlyDictionary<string, FileEntry> state)
    {
        var deletes = actions.Where(a => a.Kind == deleteKind).ToList();
        var copies = actions.Where(a => a.Kind == copyKind).ToList();

        foreach (var del in deletes)
        {
            if (!state.TryGetValue(del.RelativePath, out var oldEntry)) continue;

            var candidates = copies.Where(c =>
                sourceScan.TryGetValue(c.RelativePath, out var newEntry)
                && SameIdentity(oldEntry, newEntry)).ToList();

            if (candidates.Count != 1) continue; // 0 = khong phai doi ten; >1 = mo ho, khong ghep
            var copy = candidates[0];

            actions.Remove(del);
            actions.Remove(copy);
            copies.Remove(copy);
            actions.Add(new SyncAction(moveKind, del.RelativePath,
                $"Nhan dien doi ten: {del.RelativePath} -> {copy.RelativePath}", copy.RelativePath));
        }
    }

    private static bool SameIdentity(FileEntry a, FileEntry b)
    {
        if (a.Size != b.Size) return false;
        if (a.Hash is not null && b.Hash is not null)
            return string.Equals(a.Hash, b.Hash, StringComparison.OrdinalIgnoreCase);
        return false; // Thieu hash thi khong du bang chung de ket luan doi ten
    }
}
