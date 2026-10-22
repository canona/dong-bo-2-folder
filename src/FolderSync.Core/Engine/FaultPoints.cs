namespace FolderSync.Core.Engine;

/// <summary>
/// Ten cac diem chot chich loi (fault injection) trong 1 lan chay. CHI harness gan callback
/// (SyncSession.FaultHook / SqliteStateStore.FaultHook, mac dinh null) de mo phong crash hoac doi nguon
/// giua chung; san xuat khong gan nen hanh vi khong doi. Callback nhan (ten diem chot, duong dan lien quan hoac null).
/// </summary>
internal static class FaultPoints
{
    /// <summary>Sau khi quet xong 2 phia, truoc khi nap state / so sanh.</summary>
    public const string AfterScan = nameof(AfterScan);
    /// <summary>Da co ke hoach + qua chot xoa, truoc hanh dong dau tien.</summary>
    public const string BeforeFirstAction = nameof(BeforeFirstAction);
    /// <summary>Sau khoi ghi dau tien vao file tam .tmp-sync (file chua chep xong).</summary>
    public const string MidCopy = nameof(MidCopy);
    /// <summary>File tam da chep xong + kiem chung, chua dung vao file dich.</summary>
    public const string AfterTempWrite = nameof(AfterTempWrite);
    /// <summary>Ban cu da vao history, ngay truoc khi rename de len file dich.</summary>
    public const string BeforeRename = nameof(BeforeRename);
    /// <summary>Da rename de len file dich, chua dat lai mtime/thuoc tinh.</summary>
    public const string AfterRename = nameof(AfterRename);
    /// <summary>Ngay truoc khi doi/sao chep ban cu vao .foldersync_history.</summary>
    public const string BeforeHistoryMove = nameof(BeforeHistoryMove);
    /// <summary>Executor xong va da quet lai, chua luu state.</summary>
    public const string AfterExecuteBeforeSaveState = nameof(AfterExecuteBeforeSaveState);
    /// <summary>Giua transaction SaveState (da xoa state cu, chua ghi state moi).</summary>
    public const string DuringSaveState = nameof(DuringSaveState);

    public static readonly string[] All =
    {
        AfterScan, BeforeFirstAction, MidCopy, AfterTempWrite, BeforeRename, AfterRename,
        BeforeHistoryMove, AfterExecuteBeforeSaveState, DuringSaveState
    };
}
