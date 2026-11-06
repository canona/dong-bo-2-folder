using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

/// <summary>
/// Dong bo THU MUC RONG (thu muc co file thi tu duoc tao khi chep file, tu duoc don khi xoa het file).
/// Chi sinh hanh dong cho thu muc "la": khong chua file nao duoc quet va khong co thu muc con duoc quet -
/// tao thu muc la la tao luon ca cha; thu muc cha rong sau khi xoa la thi SyncExecutor don (PruneEmptyParents).
///
/// Quy tac an toan (giong file):
/// - 2 chieu: thu muc chi co o 1 phia ma KHONG co trong dir_state = moi tao -> tao o phia kia;
///   CO trong dir_state = phia kia da xoa -> xoa (chi khi van rong). Chua co dir_state (Job cu, lan dau)
///   thi khong bao gio suy ra "da xoa".
/// - Chi xoa thu muc RONG HOAN TOAN tren dia (ScanResult.EmptyDirs); xoa luon khong de quy, khong mat du lieu.
/// - Thu muc trung ten voi file: de logic file xu ly; rieng thu muc rong chan duong lenh chep file cung ten
///   thi xoa no truoc (truoc day PruneEmptyDirs don ca cay moi lan chay - xoa luon thu muc nguoi dung vua tao).
/// </summary>
public static class DirectoryPlanner
{
    public static List<SyncAction> Plan(
        SyncMode mode,
        bool backupPropagateDeletes,
        IReadOnlySet<string> state,
        ScanResult left,
        ScanResult right,
        IReadOnlyList<SyncAction> fileActions)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var leafL = Leaves(left);
        var leafR = Leaves(right);
        // Duong dan se duoc GHI FILE vao / XOA FILE khoi tung phia boi cac lenh file cua lan chay nay
        var fileInto = (L: new HashSet<string>(cmp), R: new HashSet<string>(cmp));
        var fileGone = (L: new HashSet<string>(cmp), R: new HashSet<string>(cmp));
        foreach (var a in fileActions)
        {
            switch (a.Kind)
            {
                case SyncActionKind.CopyLeftToRight: fileInto.R.Add(a.TargetPath ?? a.RelativePath); break;
                case SyncActionKind.CopyRightToLeft: fileInto.L.Add(a.TargetPath ?? a.RelativePath); break;
                case SyncActionKind.DeleteLeft: fileGone.L.Add(a.RelativePath); break;
                case SyncActionKind.DeleteRight: fileGone.R.Add(a.RelativePath); break;
                case SyncActionKind.MoveLeft: fileGone.L.Add(a.RelativePath); fileInto.L.Add(a.TargetPath!); break;
                case SyncActionKind.MoveRight: fileGone.R.Add(a.RelativePath); fileInto.R.Add(a.TargetPath!); break;
            }
        }

        var creates = new List<SyncAction>();
        var deletes = new List<SyncAction>();
        var paths = new HashSet<string>(left.Dirs, cmp);
        paths.UnionWith(right.Dirs);
        paths.UnionWith(state);

        foreach (var path in paths)
        {
            bool inL = left.Dirs.Contains(path), inR = right.Dirs.Contains(path), inS = state.Contains(path);

            // Thu muc rong o 1 phia chan lenh chep FILE cung ten vao phia do -> xoa truoc (rong nen khong mat gi)
            if (inL && fileInto.L.Contains(path))
            {
                if (left.EmptyDirs.Contains(path))
                    deletes.Add(new(SyncActionKind.DeleteLeft, path, "Thu muc rong trung ten file se chep", IsDirectory: true));
                continue;
            }
            if (inR && fileInto.R.Contains(path))
            {
                if (right.EmptyDirs.Contains(path))
                    deletes.Add(new(SyncActionKind.DeleteRight, path, "Thu muc rong trung ten file se chep", IsDirectory: true));
                continue;
            }
            if (inL == inR) continue;
            // Trung ten FILE o bat ky phia nao (doi file <-> thu muc): logic file + don thu muc cha da xu ly
            if (left.Entries.ContainsKey(path) || right.Entries.ContainsKey(path)) continue;

            if (inL)
            {
                bool create = mode switch
                {
                    SyncMode.TwoWay => !inS,
                    _ => true // 1 chieu: thu muc o nguon luon phai co o dich
                };
                if (create)
                {
                    if (leafL.Contains(path) && CanCreate(path, right, fileGone.R))
                        creates.Add(new(SyncActionKind.CopyLeftToRight, path,
                            mode == SyncMode.TwoWay ? "Thu muc moi o Trai" : "Thu muc rong o nguon", IsDirectory: true));
                }
                else if (left.EmptyDirs.Contains(path))
                    deletes.Add(new(SyncActionKind.DeleteLeft, path, "Da xoa thu muc o Phai", IsDirectory: true));
            }
            else
            {
                switch (mode)
                {
                    case SyncMode.TwoWay when !inS:
                        if (leafR.Contains(path) && CanCreate(path, left, fileGone.L))
                            creates.Add(new(SyncActionKind.CopyRightToLeft, path, "Thu muc moi o Phai", IsDirectory: true));
                        break;
                    case SyncMode.TwoWay:
                        if (right.EmptyDirs.Contains(path))
                            deletes.Add(new(SyncActionKind.DeleteRight, path, "Da xoa thu muc o Trai", IsDirectory: true));
                        break;
                    case SyncMode.MirrorOneWay:
                        if (right.EmptyDirs.Contains(path))
                            deletes.Add(new(SyncActionKind.DeleteRight, path, "Mirror: thu muc thua o Phai", IsDirectory: true));
                        break;
                    default: // Backup: khong dung vao dich, tru khi bat lan truyen xoa va thu muc tung duoc dong bo
                        if (backupPropagateDeletes && inS && right.EmptyDirs.Contains(path))
                            deletes.Add(new(SyncActionKind.DeleteRight, path, "Backup co lan truyen xoa: nguon da xoa thu muc", IsDirectory: true));
                        break;
                }
            }
        }

        // Xoa thu muc sau cung + sau hon truoc (cha rong ra moi don duoc); tao theo thu tu duong dan
        creates.Sort((a, b) => cmp.Compare(a.RelativePath, b.RelativePath));
        deletes.Sort((a, b) =>
        {
            int byDepth = Depth(b.RelativePath).CompareTo(Depth(a.RelativePath));
            return byDepth != 0 ? byDepth : cmp.Compare(a.RelativePath, b.RelativePath);
        });
        return creates.Concat(deletes).ToList();
    }

    /// <summary>Phia dich co FILE trung ten voi thu muc can tao (hoac voi 1 thu muc cha) ma file do khong bi xoa
    /// trong lan chay nay -> tao se loi I/O moi lan; de logic file quyet dinh.</summary>
    private static bool CanCreate(string path, ScanResult target, HashSet<string> goneOnTarget)
    {
        for (string? p = path; p is not null; p = Parent(p))
            if (target.Entries.ContainsKey(p) && !goneOnTarget.Contains(p)) return false;
        return true;
    }

    /// <summary>Thu muc khong chua file nao duoc quet va khong co thu muc con duoc quet.</summary>
    private static HashSet<string> Leaves(ScanResult scan)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var hasContent = new HashSet<string>(cmp);
        foreach (var p in scan.Entries.Keys.Concat(scan.Dirs))
            for (var parent = Parent(p); parent is not null && hasContent.Add(parent); parent = Parent(parent)) { }
        var leaves = new HashSet<string>(scan.Dirs, cmp);
        leaves.ExceptWith(hasContent);
        return leaves;
    }

    private static string? Parent(string rel)
    {
        int i = rel.LastIndexOf('/');
        return i > 0 ? rel[..i] : null;
    }

    private static int Depth(string rel) => rel.Count(c => c == '/');
}
