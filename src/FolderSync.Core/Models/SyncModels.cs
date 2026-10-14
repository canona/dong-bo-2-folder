namespace FolderSync.Core.Models;

/// <summary>Che do cua mot Job - chot 14/10/2026: moi Job chon 1 kieu.</summary>
public enum SyncMode
{
    /// <summary>1 chieu Trai -> Phai, mac dinh KHONG lan truyen xoa.</summary>
    BackupOneWay,
    /// <summary>1 chieu Trai -> Phai, Phai giong het Trai (co lan truyen xoa).</summary>
    MirrorOneWay,
    /// <summary>2 chieu Trai <-> Phai, co lan truyen xoa ca 2 chieu.</summary>
    TwoWay
}

public enum ConflictPolicy
{
    /// <summary>Ban moi hon thang, ban thua vao history.</summary>
    NewerWins,
    LeftWins,
    RightWins,
    /// <summary>Doi ten ban thua va giu ca hai.</summary>
    KeepBoth,
    /// <summary>Chi dung cho Job chay tay - hoi nguoi dung tung xung dot.</summary>
    Manual
}

/// <summary>Trang thai cua 1 file tai 1 thoi diem (trong state cu hoac ket qua quet hien tai).</summary>
public sealed record FileEntry(string RelativePath, long Size, DateTime MtimeUtc, string? Hash = null);

public enum SyncActionKind
{
    CopyLeftToRight,
    CopyRightToLeft,
    DeleteLeft,
    DeleteRight,
    /// <summary>Doi ten/di chuyen ben Trai: RelativePath (cu) -> TargetPath (moi). Sinh boi RenameDetector.</summary>
    MoveLeft,
    /// <summary>Doi ten/di chuyen ben Phai: RelativePath (cu) -> TargetPath (moi). Sinh boi RenameDetector.</summary>
    MoveRight,
    Conflict,
    Skip
}

public sealed record SyncAction(SyncActionKind Kind, string RelativePath, string Reason, string? TargetPath = null);
