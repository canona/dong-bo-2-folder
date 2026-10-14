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
    /// <summary>Chot an toan: so lenh xoa vuot nguong nay (theo % so file tham chieu - xem Execute) thi huy toan bo lan chay.</summary>
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

public enum SyncProgressStage { Planned, Started, Completed, Failed }

/// <summary>
/// Tien trinh dong bo cho UI. Planned: Plan = danh sach hanh dong se chay (Index/Action khong dung).
/// Started/Completed/Failed: Index la vi tri trong Plan; Destination = duong dan day du noi file duoc ghi
/// (copy/doi ten) hoac noi ban cu bi doi vao history (xoa).
/// </summary>
public sealed record SyncProgress(
    SyncProgressStage Stage,
    int Index,
    int Total,
    SyncAction? Action,
    string? Destination = null,
    string? Error = null,
    IReadOnlyList<SyncAction>? Plan = null)
{
    public DateTime TimeLocal { get; } = DateTime.Now;
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

    /// <param name="baselineFileCount">Moc tinh % cho chot an toan: so file trong state, hoac tong so file
    /// hien co o 2 phia neu chua co state (lan chay dau).</param>
    /// <param name="progress">Bao Started/Completed/Failed cho tung hanh dong (tru Skip/Conflict).</param>
    public SyncResult Execute(IReadOnlyList<SyncAction> actions, int baselineFileCount,
        IProgress<SyncProgress>? progress = null)
    {
        var result = new SyncResult();

        // --- Chot an toan xoa hang loat: kiem tra truoc khi thuc thi bat ky lenh nao ---
        var deletes = actions.Count(a => a.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight);
        if (!_options.Force && deletes > 0)
        {
            bool overPercent = baselineFileCount > 0 && deletes * 100.0 / baselineFileCount > _options.MaxDeletePercent;
            bool overAbsolute = deletes > _options.MaxDeleteAbsolute;
            if (overPercent || overAbsolute)
            {
                result.Aborted = true;
                result.AbortReason =
                    $"Chot an toan: {deletes} lenh xoa vuot nguong ({_options.MaxDeletePercent}% cua {baselineFileCount} file tham chieu, toi da {_options.MaxDeleteAbsolute}). " +
                    "Co the do o bi rut/mat ket noi hoac state hong. Huy toan bo, chua dong vao file nao.";
                return result;
            }
        }

        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            bool silent = action.Kind is SyncActionKind.Skip or SyncActionKind.Conflict;
            if (!silent) progress?.Report(new SyncProgress(SyncProgressStage.Started, i, actions.Count, action));
            string? destination = null;
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CopyLeftToRight:
                        // TargetPath (neu co) la duong dan dich khac duong dan nguon - dung cho ban "conflict copy"
                        destination = Full(_rightRoot, action.TargetPath ?? action.RelativePath);
                        AtomicCopy(Full(_leftRoot, action.RelativePath), destination,
                                   _rightRoot, action.TargetPath ?? action.RelativePath);
                        result.Copied++;
                        break;
                    case SyncActionKind.CopyRightToLeft:
                        destination = Full(_leftRoot, action.TargetPath ?? action.RelativePath);
                        AtomicCopy(Full(_rightRoot, action.RelativePath), destination,
                                   _leftRoot, action.TargetPath ?? action.RelativePath);
                        result.Copied++;
                        break;
                    case SyncActionKind.DeleteLeft:
                        destination = DeleteWithHistory(Full(_leftRoot, action.RelativePath), _leftRoot, action.RelativePath);
                        result.Deleted++;
                        break;
                    case SyncActionKind.DeleteRight:
                        destination = DeleteWithHistory(Full(_rightRoot, action.RelativePath), _rightRoot, action.RelativePath);
                        result.Deleted++;
                        break;
                    case SyncActionKind.MoveLeft:
                        destination = MoveWithin(_leftRoot, action);
                        result.Moved++;
                        break;
                    case SyncActionKind.MoveRight:
                        destination = MoveWithin(_rightRoot, action);
                        result.Moved++;
                        break;
                    case SyncActionKind.Conflict:
                        result.Conflicts++;
                        break;
                    default:
                        result.Skipped++;
                        break;
                }
                if (!silent)
                    progress?.Report(new SyncProgress(SyncProgressStage.Completed, i, actions.Count, action, destination));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"{action.Kind} {action.RelativePath}: {ex.Message}");
                progress?.Report(new SyncProgress(SyncProgressStage.Failed, i, actions.Count, action, destination, ex.Message));
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

    /// <returns>Noi file bi doi vao history, hoac null neu xoa vinh vien / file khong con.</returns>
    private string? DeleteWithHistory(string full, string root, string rel)
    {
        if (!File.Exists(full)) return null;
        if (_options.DeleteMode == DeleteMode.MoveToHistory)
        {
            var hist = HistoryPath(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
            File.Move(full, hist, overwrite: true);
            return hist;
        }
        File.Delete(full);
        return null;
    }

    private void PreserveExisting(string destFull, string destRoot, string rel)
    {
        if (_options.DeleteMode != DeleteMode.MoveToHistory) return;
        var hist = HistoryPath(destRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
        File.Copy(destFull, hist, overwrite: true);
    }

    private string MoveWithin(string root, SyncAction action)
    {
        var from = Full(root, action.RelativePath);
        var to = Full(root, action.TargetPath!);
        if (!File.Exists(from)) throw new IOException($"Khong tim thay file nguon de doi ten: {action.RelativePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        // Doi ten chi khac hoa/thuong: "to" chinh la file "from" - khong sao luu vao history
        bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(to)) PreserveExisting(to, root, action.TargetPath!);
        File.Move(from, to, overwrite: true);
        return to;
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
