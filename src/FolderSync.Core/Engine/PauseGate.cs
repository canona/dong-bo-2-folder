namespace FolderSync.Core.Engine;

/// <summary>
/// Tam dung / tiep tuc 1 lan dong bo dang chay. Chi dung o DIEM AN TOAN: truoc moi hanh dong va giua 2 khoi
/// ghi file tam .tmp-sync (cung cho voi BandwidthThrottle) - thu tu copy nguyen tu khong doi, file dich
/// khong bao gio bi ghi do dang. Huy trong luc dang tam dung -> OperationCanceledException -> AtomicCopy
/// xoa file tam nhu moi lan huy khac.
/// An toan da luong: UI goi Pause/Resume, thread chep goi WaitWhilePaused.
/// </summary>
public sealed class PauseGate
{
    // Khong Dispose: runner cu (bi thay khi sua Job) co the van dang cho tren gate nay
    private readonly ManualResetEventSlim _open = new(true);

    public bool IsPaused => !_open.IsSet;
    public void Pause() => _open.Reset();
    public void Resume() => _open.Set();

    /// <exception cref="OperationCanceledException">Huy trong luc dang tam dung.</exception>
    public void WaitWhilePaused(CancellationToken ct)
    {
        if (_open.IsSet) return;
        _open.Wait(ct);
    }
}
