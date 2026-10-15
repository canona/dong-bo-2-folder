using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

public sealed record ScanResult(
    IReadOnlyDictionary<string, FileEntry> Entries,
    IReadOnlyList<string> Errors,
    int SkippedLinks,
    /// <summary>Duong dan tuong doi (file hoac thu muc) khong doc duoc - noi dung ben duoi KHONG duoc biet.</summary>
    IReadOnlyList<string> FailedPaths,
    /// <summary>Thu muc goc khong ton tai (USB chua cam, sai duong dan...).</summary>
    bool RootMissing = false);

/// <summary>
/// Quet cay thu muc thanh danh sach FileEntry theo duong dan tuong doi (chuan hoa '/').
/// Nguyen tac (Ke hoach muc 2.1):
/// - Ton trong SyncFilter (file tam Office, .git, history cua chinh app...).
/// - KHONG di theo symlink/junction (tranh vong lap vo han) - dem vao SkippedLinks.
/// - Loi truy cap 1 file/thu muc khong lam sap ca lan quet - ghi vao Errors va FailedPaths
///   (SyncSession dung FailedPaths de KHONG coi file khong doc duoc la "da xoa").
/// - File bi xoa giua luc quet (race) cung chi ghi loi, khong crash.
/// </summary>
public sealed class FolderScanner
{
    private readonly SyncFilter _filter;

    public FolderScanner(SyncFilter? filter = null) => _filter = filter ?? new SyncFilter();

    /// <param name="computeHashes">true = hash MOI file (cham, doc toan bo noi dung - chi dung khi that can).
    /// false = khong hash; van gan hash tu <paramref name="hashCache"/> neu file khop.</param>
    /// <param name="hashCache">Ket qua quet truoc cua CHINH folder nay: file cung size + mtime (chinh xac
    /// tung tick) thi dung lai hash cu thay vi doc lai ca file. Gioi han: file bi sua ma giu nguyen
    /// size + mtime se khong bi phat hien.</param>
    /// <param name="onProgress">(so file da quet, thu muc dang quet) - bao toi da ~6 lan/giay.</param>
    public ScanResult Scan(string root, bool computeHashes = false,
        IReadOnlyDictionary<string, FileEntry>? hashCache = null,
        Action<int, string>? onProgress = null, CancellationToken ct = default)
    {
        var entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var failed = new List<string>();
        int skippedLinks = 0;

        if (!Directory.Exists(root))
        {
            errors.Add($"Thu muc khong ton tai: {root}");
            return new ScanResult(entries, errors, 0, failed, RootMissing: true);
        }

        var pending = new Stack<string>();
        pending.Push(root);
        var lastReport = DateTime.UtcNow;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            if (onProgress is not null && DateTime.UtcNow - lastReport > ReportInterval)
            {
                onProgress(entries.Count, Normalize(Path.GetRelativePath(root, dir)));
                lastReport = DateTime.UtcNow;
            }

            // 1 lan liet ke thu muc tra luon size/mtime/thuoc tinh (FindFirstFile) - KHONG goi them
            // FileInfo/GetAttributes tung file: tren USB / o mang moi lan goi them la 1 vong I/O cham.
            List<FileSystemInfo> items;
            try
            {
                items = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                errors.Add($"Khong doc duoc thu muc {dir}: {ex.Message}");
                failed.Add(Normalize(Path.GetRelativePath(root, dir)));
                continue;
            }

            foreach (var item in items)
            {
                var rel = Normalize(Path.GetRelativePath(root, item.FullName));
                try
                {
                    var attrs = item.Attributes;
                    if (item is DirectoryInfo)
                    {
                        if ((attrs & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                        // Thu muc bi loc (vd .git, .foldersync_history) thi khong di vao
                        if (_filter.IsDirectoryIncluded(rel)) pending.Push(item.FullName);
                        continue;
                    }

                    if (!_filter.IsIncluded(rel)) continue;
                    if ((attrs & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                    if (_filter.ExcludeHidden && (attrs & FileAttributes.Hidden) != 0) continue;
                    if (_filter.ExcludeSystem && (attrs & FileAttributes.System) != 0) continue;

                    var info = (FileInfo)item;
                    long size = info.Length;
                    var mtime = info.LastWriteTimeUtc;
                    string? hash = hashCache is not null && hashCache.TryGetValue(rel, out var cached)
                                   && cached.Hash is not null && cached.Size == size && cached.MtimeUtc == mtime
                        ? cached.Hash
                        : null;
                    if (hash is null && computeHashes) hash = FileHasher.ComputeHash(item.FullName, ct);
                    entries[rel] = new FileEntry(rel, size, mtime, hash);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"Bo qua {rel}: {ex.Message}");
                    failed.Add(rel);
                }
            }
        }

        onProgress?.Invoke(entries.Count, "");
        return new ScanResult(entries, errors, skippedLinks, failed);
    }

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(150);

    internal static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/');
}
