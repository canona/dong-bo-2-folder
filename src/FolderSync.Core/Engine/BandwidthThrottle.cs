using System.Diagnostics;

namespace FolderSync.Core.Engine;

/// <summary>
/// Gioi han toc do chep (bang thong) cho 1 lan chay cua 1 Job - token bucket.
/// - 1 instance cho CA lan chay (SyncExecutor.Execute): gioi han tinh chung moi file, khong reset theo file.
/// - Chi CHEN THOI GIAN CHO vao vong ghi file tam .tmp-sync: thu tu copy nguyen tu (tmp -> kiem size/hash
///   -> rename) khong doi; huy trong luc cho -> OperationCanceledException -> AtomicCopy xoa file tam nhu cu.
/// - Cho phep "chay bu" toi da 1 giay du lieu sau khi dung yen (vd dang kiem hash file lon): neu khong,
///   dung 10 giay roi se duoc xa 10 giay du lieu mot luc, vuot gioi han xa.
/// Khong an toan da luong - moi SyncExecutor.Execute dung 1 instance tren 1 thread.
/// </summary>
public sealed class BandwidthThrottle
{
    private const int MinChunk = 64 * 1024;
    private const int MaxChunk = 1024 * 1024;

    private readonly long _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _bytes; // so byte "da tieu" tinh tu luc bat dau (co the dich len khi bo bot luong bu)

    public BandwidthThrottle(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        _bytesPerSecond = bytesPerSecond;
    }

    public long BytesPerSecond => _bytesPerSecond;

    /// <summary>
    /// Kich thuoc khoi doc/ghi phu hop: ~1/10 giay du lieu (tien trinh va toc do hien muot),
    /// trong khoang 64 KB - 1 MB.
    /// </summary>
    public int ChunkSize => (int)Math.Clamp(_bytesPerSecond / 10, MinChunk, MaxChunk);

    /// <summary>Ghi nhan vua chep <paramref name="bytes"/> byte; cho neu dang nhanh hon gioi han.</summary>
    /// <exception cref="OperationCanceledException">Huy trong luc cho.</exception>
    public void Consume(long bytes, CancellationToken ct)
    {
        double elapsed = _clock.Elapsed.TotalSeconds;
        // Gioi han luong "chay bu" con lai sau khi dung yen: toi da 1 giay du lieu
        double allowed = elapsed * _bytesPerSecond;
        if (allowed - _bytes > _bytesPerSecond) _bytes = allowed - _bytesPerSecond;

        _bytes += bytes;
        var wait = TimeSpan.FromSeconds(_bytes / _bytesPerSecond - elapsed);
        if (wait <= TimeSpan.Zero) return;
        // WaitOne tra ve true ngay khi bi huy -> dung luon, khong cho het
        if (ct.WaitHandle.WaitOne(wait)) ct.ThrowIfCancellationRequested();
    }
}
