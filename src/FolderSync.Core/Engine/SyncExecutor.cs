using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

public enum DeleteMode
{
    /// <summary>Mac dinh an toan: doi file vao .foldersync_history/<thoi gian>/ truoc khi xoa/ghi de.</summary>
    MoveToHistory,
    Permanent
}

public sealed record SyncOptions(
    DeleteMode DeleteMode = DeleteMode.MoveToHistory,
    /// <summary>Chot an toan: so lenh xoa vuot nguong nay (theo % so file trong state) thi huy toan bo lan chay.</summary>
    double MaxDeletePercent = 50.0,
    int MaxDeleteAbsolute = 1000,
    bool Force = false);

public sealed class SyncResult
{
    public int Copied { get; internal set; }
    public int Deleted { get; internal set; }
    public int Moved { get; internal set; }
    public int Conflicts { get; internal set; }
    public int Skipped { get; internal set; }
    public List<string> Errors { get; } = new();
    public bool Aborted { get; internal set; }
    public string? AbortReason { get; internal set; }
}

/// <summary>
/// Thuc thi danh sach hanh dong len 2 folder that (Ke hoach muc 2.1, 2.3f):
/// - Copy NGUYEN TU: ghi file tam ".tmp-sync" cung thu muc dich -> kiem chung size/hash -> doi ten de len.
///   Mat dien/rut USB giua chung cung khong lam hong file dich dang tot.
/// - Ghi de hoac xoa (che do MoveToHistory): ban cu duoc doi vao history truoc, khoi phuc duoc.
/// - Chot an toan: qua nhieu lenh xoa -> huy toan bo TRUOC khi dong vao bat ky file nao (tru khi Force).
/// - 1 file loi khong lam sap ca lan chay: ghi Errors va tiep tuc, state chi luu phan da hoi tu (SyncSession xu ly).
/// </summary>
public sealed class SyncExecutor
{
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly SyncOptions _options;
    // Kem mili giay de moi lan chay la 1 thu muc history rieng, khong bao gio trung lan chay truoc
    private readonly string _historyStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    public SyncExecutor(string leftRoot, string rightRoot, SyncOptions? options = null)
    {
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        _options = options ?? new SyncOptions();
    }

    private string Full(string root, string rel)
        => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string HistoryPath(string root, string rel)
        => Full(root, $".foldersync_history/{_historyStamp}/{rel}");

    public SyncResult Execute(IReadOnlyList<SyncAction> actions, int stateFileCount)
    {
        var result = new SyncResult();

        // --- Chot an toan xoa hang loat: kiem tra truoc khi thuc thi bat ky lenh nao ---
        var deletes = actions.Count(a => a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight);
        if (!_options.Force && deletes > 0)
        {
            bool overPercent = stateFileCount > 0 && deletes * 100.0 / stateFileCount > _options.MaxDeletePercent;
            bool overAbsolute = deletes > _options.MaxDeleteAbsolute;
            if (overPercent || overAbsolute)
            {
                result.Aborted = true;
                result.AbortReason =
                    $"Chot an toan: {deletes} lenh xoa vuot nguong ({_options.MaxDeletePercent}% cua {stateFileCount} file trong state, toi da {_options.MaxDeleteAbsolute}). " +
                    "Co the do o bi rut/mat ket noi hoac state hong. Huy toan bo, chua dong vao file nao.";
                return result;
            }
        }

        foreach (var action in actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CopyLeftToRight:
                        // TargetPath (neu co) la duong dan dich khac duong dan nguon - dung cho ban "conflict copy"
                        AtomicCopy(Full(_leftRoot, action.RelativePath),
                                   Full(_rightRoot, action.TargetPath ?? action.RelativePath),
                                   _rightRoot, action.TargetPath ?? action.RelativePath);
                        result.Copied++;
                        break;
                    case SyncActionKind.CopyRightToLeft:
                        AtomicCopy(Full(_rightRoot, action.RelativePath),
                                   Full(_leftRoot, action.TargetPath ?? action.RelativePath),
                                   _leftRoot, action.TargetPath ?? action.RelativePath);
                        result.Copied++;
                        break;
                    case SyncActionKind.DeleteLeft:
                        DeleteWithHistory(Full(_leftRoot, action.RelativePath), _leftRoot, action.RelativePath);
                        result.Deleted++;
                        break;
                    case SyncActionKind.DeleteRight:
                        DeleteWithHistory(Full(_rightRoot, action.RelativePath), _rightRoot, action.RelativePath);
                        result.Deleted++;
                        break;
                    case SyncActionKind.MoveLeft:
                        MoveWithin(_leftRoot, action);
                        result.Moved++;
                        break;
                    case SyncActionKind.MoveRight:
                        MoveWithin(_rightRoot, action);
                        result.Moved++;
                        break;
                    case SyncActionKind.Conflict:
                        result.Conflicts++;
                        break;
                    default:
                        result.Skipped++;
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"{action.Kind} {action.RelativePath}: {ex.Message}");
            }
        }

        PruneEmptyDirs(_leftRoot);
        PruneEmptyDirs(_rightRoot);
        return result;
    }

    private void AtomicCopy(string sourceFull, string destFull, string destRoot, string rel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
        var tmpFull = destFull + ".tmp-sync";

        using (var src = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dst = new FileStream(tmpFull, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            src.CopyTo(dst, 81920);
        }

        // Kiem chung toan ven file tam truoc khi de len dich
        var srcInfo = new FileInfo(sourceFull);
        if (new FileInfo(tmpFull).Length != srcInfo.Length)
        {
            File.Delete(tmpFull);
            throw new IOException($"Kiem chung that bai (lech kich thuoc) khi copy {rel}");
        }
        var srcHash = FileHasher.ComputeHash(sourceFull);
        if (!FileHasher.Verify(tmpFull, srcHash))
        {
            File.Delete(tmpFull);
            throw new IOException($"Kiem chung that bai (lech hash) khi copy {rel}");
        }

        if (File.Exists(destFull)) PreserveExisting(destFull, destRoot, rel);
        File.Move(tmpFull, destFull, overwrite: true);
        // Giu nguyen mtime cua nguon - day la tin hieu phat hien thay doi o lan quet sau
        File.SetLastWriteTimeUtc(destFull, srcInfo.LastWriteTimeUtc);
    }

    private void DeleteWithHistory(string full, string root, string rel)
    {
        if (!File.Exists(full)) return;
        if (_options.DeleteMode == DeleteMode.MoveToHistory)
        {
            var hist = HistoryPath(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
            File.Move(full, hist, overwrite: true);
        }
        else
        {
            File.Delete(full);
        }
    }

    private void PreserveExisting(string destFull, string destRoot, string rel)
    {
        if (_options.DeleteMode != DeleteMode.MoveToHistory) return;
        var hist = HistoryPath(destRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
        File.Copy(destFull, hist, overwrite: true);
    }

    private void MoveWithin(string root, SyncAction action)
    {
        var from = Full(root, action.RelativePath);
        var to = Full(root, action.TargetPath!);
        if (!File.Exists(from)) throw new IOException($"Khong tim thay file nguon de doi ten: {action.RelativePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (File.Exists(to)) PreserveExisting(to, root, action.TargetPath!);
        File.Move(from, to, overwrite: true);
    }

    private static void PruneEmptyDirs(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(".foldersync_history", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
        catch (IOException) { /* don dep thu muc rong la viec phu - bo qua khi bi khoa */ }
    }
}
