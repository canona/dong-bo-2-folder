namespace FolderSync.Core.Engine;

public sealed record HistoryEntry(string Stamp, string RelativePath, long Size, DateTime FileMtimeUtc, string FullPath);

/// <summary>
/// Quan ly folder lich su .foldersync_history/<yyyyMMdd-HHmmss>/<duong dan goc> trong moi folder goc.
/// Moi file bi xoa hoac bi ghi de (che do MoveToHistory) deu nam tai day - HistoryService liet ke va khoi phuc.
/// Khoi phuc cung theo nguyen tac an toan: ghi file tam roi doi ten de, va ban dang co (neu khac)
/// duoc sao vao history truoc khi bi ghi de.
/// </summary>
public static class HistoryService
{
    public const string HistoryFolderName = ".foldersync_history";

    public static IReadOnlyList<HistoryEntry> List(string root)
    {
        var historyRoot = Path.Combine(root, HistoryFolderName);
        var result = new List<HistoryEntry>();
        if (!Directory.Exists(historyRoot)) return result;

        foreach (var stampDir in Directory.GetDirectories(historyRoot))
        {
            var stamp = Path.GetFileName(stampDir);
            foreach (var file in Directory.GetFiles(stampDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stampDir, file).Replace('\\', '/');
                var info = new FileInfo(file);
                result.Add(new HistoryEntry(stamp, rel, info.Length, info.LastWriteTimeUtc, file));
            }
        }
        return result.OrderByDescending(e => e.Stamp)
                     .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    /// <summary>
    /// Don dep history: xoa cac thu muc stamp co ngay (8 ky tu dau yyyyMMdd) cu hon keepDays.
    /// Tra ve so stamp da xoa. keepDays <= 0 = khong don. Stamp khong parse duoc ngay thi giu nguyen.
    /// </summary>
    public static int Cleanup(string root, int keepDays)
    {
        if (keepDays <= 0) return 0;
        var historyRoot = Path.Combine(root, HistoryFolderName);
        if (!Directory.Exists(historyRoot)) return 0;
        var cutoff = DateTime.Today.AddDays(-keepDays);
        int removed = 0;
        foreach (var stampDir in Directory.GetDirectories(historyRoot))
        {
            var stamp = Path.GetFileName(stampDir);
            if (stamp.Length < 8) continue;
            if (!DateTime.TryParseExact(stamp[..8], "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var day)) continue;
            if (day < cutoff)
            {
                Directory.Delete(stampDir, recursive: true);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Khoi phuc 1 ban trong history ve vi tri goc. Tra ve duong dan dich.</summary>
    public static string Restore(string root, HistoryEntry entry)
    {
        if (!File.Exists(entry.FullPath))
            throw new FileNotFoundException("Ban history khong con ton tai", entry.FullPath);

        var destFull = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);

        // QUAN TRONG - thu tu: chup noi dung ban history vao file tam TRUOC,
        // roi moi sao luu file dich hien tai. Neu sao luu truoc va thu muc history trung ten,
        // chinh file dang khoi phuc co the bi ghi de boi ban sao luu.
        var tmpFull = destFull + ".tmp-sync";
        File.Copy(entry.FullPath, tmpFull, overwrite: true);
        if (new FileInfo(tmpFull).Length != entry.Size)
        {
            File.Delete(tmpFull);
            throw new IOException("Kiem chung that bai khi khoi phuc (lech kich thuoc)");
        }

        // Neu dang co file o dich, giu lai no vao history truoc (khong mat du lieu khi khoi phuc nham)
        if (File.Exists(destFull))
        {
            var backupStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var backupPath = Path.Combine(root, HistoryFolderName, backupStamp,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            // Phong thu: khong bao gio ghi de len chinh ban dang khoi phuc
            if (!string.Equals(backupPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(destFull, backupPath, overwrite: true);
            }
        }

        File.Move(tmpFull, destFull, overwrite: true);
        File.SetLastWriteTimeUtc(destFull, entry.FileMtimeUtc);
        return destFull;
    }
}
