using System.Security.Cryptography;

namespace FolderSync.Core.Engine;

/// <summary>
/// Hash noi dung file bang SHA-256 (BCL, khong them phu thuoc).
/// Dung cho: kiem chung toan ven sau copy va nhan dien doi ten.
/// Ghi chu: BLAKE3 nhanh hon va co the thay o giai doan toi uu hieu nang (P3),
/// interface giu nguyen nen viec thay khong anh huong engine.
/// </summary>
public static class FileHasher
{
    public static string ComputeHash(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool Verify(string fullPath, string expectedHash)
        => string.Equals(ComputeHash(fullPath), expectedHash, StringComparison.OrdinalIgnoreCase);
}
