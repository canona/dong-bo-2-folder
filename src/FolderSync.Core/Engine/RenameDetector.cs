using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

/// <summary>
/// Nhan dien doi ten / di chuyen (Ke hoach muc 2.3c):
/// Sau Analyze, cap [Delete o phia X tai A] + [Copy vao phia X tai B] ma NOI DUNG HIEN TAI cua file A
/// o phia X giong file nguon B (cung size + cung hash) duoc gop thanh 1 lenh Move o phia X,
/// thay vi xoa roi copy lai tu dau (ton bang thong va mat lich su tren USB cham).
/// - So noi dung file A THAT o phia dich (khong so voi hash trong state): Move chi dung khi A dang
///   chua dung noi dung can co o B - ke ca khi A da bi sua o dich (Mirror) hay state khong luu hash.
/// - Chi ghep khi khop 1-1 duy nhat; mo ho (nhieu ung vien giong nhau) thi giu nguyen cap goc.
/// - Thieu hash (chua tinh) thi khong ket luan. SyncSession chi tinh hash cho cac file co the ghep
///   (HashPlanner.ForRenames) nen khong phai doc toan bo du lieu.
/// - Lap chi muc theo (size, hash): O(n) - ban cu so moi lenh xoa voi moi lenh chep (O(n^2)).
/// </summary>
public static class RenameDetector
{
    public static IReadOnlyList<SyncAction> Apply(
        IReadOnlyList<SyncAction> actions,
        IReadOnlyDictionary<string, FileEntry> state,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        // File bi doi ten o Trai: DeleteRight(A) + CopyLeftToRight(B), noi dung Phai[A] == Trai[B]
        var result = TryPair(actions, SyncActionKind.DeleteRight, SyncActionKind.CopyLeftToRight,
            SyncActionKind.MoveRight, sourceScan: left, targetScan: right, state);
        // File bi doi ten o Phai: DeleteLeft(A) + CopyRightToLeft(B), noi dung Trai[A] == Phai[B]
        return TryPair(result, SyncActionKind.DeleteLeft, SyncActionKind.CopyRightToLeft,
            SyncActionKind.MoveLeft, sourceScan: right, targetScan: left, state);
    }

    private static IReadOnlyList<SyncAction> TryPair(
        IReadOnlyList<SyncAction> actions,
        SyncActionKind deleteKind, SyncActionKind copyKind, SyncActionKind moveKind,
        IReadOnlyDictionary<string, FileEntry> sourceScan,
        IReadOnlyDictionary<string, FileEntry> targetScan,
        IReadOnlyDictionary<string, FileEntry> state)
    {
        // Chi muc cac lenh chep theo noi dung nguon
        var copiesById = new Dictionary<(long, string), List<int>>();
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (a.Kind != copyKind || a.TargetPath is not null) continue; // conflict copy khong phai doi ten
            if (!sourceScan.TryGetValue(a.RelativePath, out var src) || src.Hash is null) continue;
            var key = (src.Size, src.Hash.ToUpperInvariant());
            if (!copiesById.TryGetValue(key, out var list)) copiesById[key] = list = new List<int>();
            list.Add(i);
        }
        if (copiesById.Count == 0) return actions;

        var removed = new HashSet<int>();
        var moves = new List<SyncAction>();
        for (int i = 0; i < actions.Count; i++)
        {
            var del = actions[i];
            if (del.Kind != deleteKind || !state.ContainsKey(del.RelativePath)) continue;
            if (!targetScan.TryGetValue(del.RelativePath, out var target) || target.Hash is null) continue;
            if (!copiesById.TryGetValue((target.Size, target.Hash.ToUpperInvariant()), out var candidates)) continue;

            var free = candidates.Where(j => !removed.Contains(j)).ToList();
            if (free.Count != 1) continue; // 0 = da ghep het; >1 = mo ho, khong ghep
            var copy = actions[free[0]];
            // Doi ten xuyen kieu (file 'a/x' -> file 'a', hoac file 'a' -> 'a/x'): Move khong tao duoc duong dan dich
            // vi thu muc/file cung ten van con -> giu cap Delete+Copy, SyncExecutor xoa truoc roi chep.
            if (IsAncestor(copy.RelativePath, del.RelativePath) || IsAncestor(del.RelativePath, copy.RelativePath)) continue;
            removed.Add(i);
            removed.Add(free[0]);
            moves.Add(new SyncAction(moveKind, del.RelativePath,
                $"Nhan dien doi ten: {del.RelativePath} -> {copy.RelativePath}", copy.RelativePath));
        }
        if (moves.Count == 0) return actions;

        var result = new List<SyncAction>(actions.Count);
        for (int i = 0; i < actions.Count; i++)
            if (!removed.Contains(i)) result.Add(actions[i]);
        result.AddRange(moves);
        return result;
    }

    private static bool IsAncestor(string path, string other) =>
        other.Length > path.Length && other[path.Length] == '/'
        && other.StartsWith(path, StringComparison.OrdinalIgnoreCase);
}
