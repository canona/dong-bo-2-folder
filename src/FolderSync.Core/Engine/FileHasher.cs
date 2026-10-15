using System.Security.Cryptography;

namespace FolderSync.Core.Engine;

/// <summary>
/// Hash noi dung file bang SHA-256 (BCL, khong them phu thuoc).
/// Dung cho: kiem chung toan ven sau copy, so noi dung khi size bang nhau ma mtime khac, nhan dien doi ten.
/// Ghi chu: BLAKE3 nhanh hon va co the thay o giai doan toi uu hieu nang,
/// interface giu nguyen nen viec thay khong anh huong engine.
/// </summary>
public static class FileHasher
{
    private const int BufferSize = 1024 * 1024;

    public static string ComputeHash(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Hash co the HUY giua chung (file vai GB khong bat nguoi dung cho het file) va bao so byte da doc
    /// (onBytes nhan so byte vua doc them, de UI tinh tien trinh "dang so noi dung").
    /// </summary>
    public static string ComputeHash(string fullPath, CancellationToken ct, Action<long>? onBytes = null)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.AppendData(buffer, 0, read);
            onBytes?.Invoke(read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    public static bool Verify(string fullPath, string expectedHash)
        => string.Equals(ComputeHash(fullPath), expectedHash, StringComparison.OrdinalIgnoreCase);
}
