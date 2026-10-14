using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

/// <summary>
/// Bien cac hanh dong Conflict thanh hanh dong cu the theo chinh sach cua Job (Ke hoach muc 2.3e, 8.1).
/// Chay sau RenameDetector, truoc Executor. Policy Manual = giu nguyen Conflict (Executor chi dem, khong dong vao file).
///
/// Quy tac:
/// - Sua/Sua (hoac Tao/Tao): NewerWins so mtime (lech trong dung sai 2s thi rut ve KeepBoth),
///   LeftWins/RightWins copy thang 1 chieu, KeepBoth giu ban Trai lam chuan + ban Phai thanh "conflict copy" o ca 2 phia.
/// - Xoa/Sua: MOI chinh sach tu dong deu giu ban da sua (copy tro lai phia da xoa) - khong bao gio de mat ban da sua.
/// </summary>
public static class ConflictResolver
{
    private static readonly TimeSpan TieTolerance = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<SyncAction> Resolve(
        IReadOnlyList<SyncAction> actions,
        ConflictPolicy policy,
        IReadOnlyDictionary<string, FileEntry> state,
        IReadOnlyDictionary<string, FileEntry> left,
        IReadOnlyDictionary<string, FileEntry> right)
    {
        if (policy == ConflictPolicy.Manual) return actions;

        var result = new List<SyncAction>();
        foreach (var action in actions)
        {
            if (action.Kind != SyncActionKind.Conflict)
            {
                result.Add(action);
                continue;
            }

            var path = action.RelativePath;
            left.TryGetValue(path, out var l);
            right.TryGetValue(path, out var r);
            bool inState = state.ContainsKey(path);

            // --- Xoa/Sua: 1 phia da xoa, phia kia da sua -> giu ban da sua ---
            if (inState && (l is null) != (r is null))
            {
                result.Add(l is not null
                    ? new SyncAction(SyncActionKind.CopyLeftToRight, path, "Xoa/Sua: giu ban da sua o Trai")
                    : new SyncAction(SyncActionKind.CopyRightToLeft, path, "Xoa/Sua: giu ban da sua o Phai"));
                continue;
            }

            if (l is null || r is null)
            {
                result.Add(action); // Khong du thong tin - giu xung dot cho nguoi dung quyet
                continue;
            }

            // --- Sua/Sua hoac Tao/Tao ---
            switch (policy)
            {
                case ConflictPolicy.LeftWins:
                    result.Add(new SyncAction(SyncActionKind.CopyLeftToRight, path, "Xung dot: Trai thang (policy)"));
                    break;
                case ConflictPolicy.RightWins:
                    result.Add(new SyncAction(SyncActionKind.CopyRightToLeft, path, "Xung dot: Phai thang (policy)"));
                    break;
                case ConflictPolicy.KeepBoth:
                    AddKeepBoth(result, path, r);
                    break;
                default: // NewerWins
                    var delta = l.MtimeUtc - r.MtimeUtc;
                    if (delta.Duration() <= TieTolerance)
                        AddKeepBoth(result, path, r);
                    else if (delta > TimeSpan.Zero)
                        result.Add(new SyncAction(SyncActionKind.CopyLeftToRight, path, "Xung dot: Trai moi hon"));
                    else
                        result.Add(new SyncAction(SyncActionKind.CopyRightToLeft, path, "Xung dot: Phai moi hon"));
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// KeepBoth: ban Trai la ban chuan; ban Phai duoc luu thanh conflict copy o ca hai phia.
    /// Thu tu quan trong: (1) dem ban Phai ve Trai duoi ten conflict, (2) ban Trai thang sang Phai,
    /// (3) conflict copy sang Phai - sau cung ca 2 phia giong het nhau, state hoi tu sach.
    /// </summary>
    private static void AddKeepBoth(List<SyncAction> result, string path, FileEntry rightEntry)
    {
        var conflictName = ConflictName(path, rightEntry.MtimeUtc);
        result.Add(new SyncAction(SyncActionKind.CopyRightToLeft, path,
            $"Xung dot: giu ban Phai thanh conflict copy", conflictName));
        result.Add(new SyncAction(SyncActionKind.CopyLeftToRight, path,
            "Xung dot: ban Trai la ban chuan"));
        result.Add(new SyncAction(SyncActionKind.CopyLeftToRight, conflictName,
            "Xung dot: dua conflict copy sang Phai", conflictName));
    }

    /// <summary>"thu-muc/file.txt" -> "thu-muc/file (conflict 2026-10-14 153000).txt" theo mtime ban bi thua.</summary>
    internal static string ConflictName(string relativePath, DateTime mtimeUtc)
    {
        var dir = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(relativePath);
        var stamped = $"{name} (conflict {mtimeUtc.ToLocalTime():yyyy-MM-dd HHmmss}){ext}";
        var combined = string.IsNullOrEmpty(dir) ? stamped : Path.Combine(dir, stamped);
        return combined.Replace('\\', '/');
    }
}
