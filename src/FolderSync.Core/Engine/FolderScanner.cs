using FolderSync.Core.Models;

namespace FolderSync.Core.Engine;

public sealed record ScanResult(
    IReadOnlyDictionary<string, FileEntry> Entries,
    IReadOnlyList<string> Errors,
    int SkippedLinks);

/// <summary>
/// Quet cay thu muc thanh danh sach FileEntry theo duong dan tuong doi (chuan hoa '/').
/// Nguyen tac (Ke hoach muc 2.1):
/// - Ton trong SyncFilter (file tam Office, .git, history cua chinh app...).
/// - KHONG di theo symlink/junction (tranh vong lap vo han) - dem vao SkippedLinks.
/// - Loi truy cap 1 file/thu muc khong lam sap ca lan quet - ghi vao Errors.
/// - File bi xoa giua luc quet (race) cung chi ghi loi, khong crash.
/// </summary>
public sealed class FolderScanner
{
    private readonly SyncFilter _filter;

    public FolderScanner(SyncFilter? filter = null) => _filter = filter ?? new SyncFilter();

    public ScanResult Scan(string root, bool computeHashes = false)
    {
        var entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        int skippedLinks = 0;

        if (!Directory.Exists(root))
        {
            errors.Add($"Thu muc khong ton tai: {root}");
            return new ScanResult(entries, errors, 0);
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subDirs, files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                errors.Add($"Khong doc duoc thu muc {dir}: {ex.Message}");
                continue;
            }

            foreach (var sub in subDirs)
            {
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"Khong kiem tra duoc {sub}: {ex.Message}");
                    continue;
                }
                // Thu muc bi loc (vd .git, .foldersync_history) thi khong di vao
                var probe = Normalize(Path.GetRelativePath(root, sub)) + "/_probe";
                if (_filter.IsIncluded(probe)) pending.Push(sub);
            }

            foreach (var file in files)
            {
                var rel = Normalize(Path.GetRelativePath(root, file));
                if (!_filter.IsIncluded(rel)) continue;
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                    string? hash = computeHashes ? FileHasher.ComputeHash(file) : null;
                    entries[rel] = new FileEntry(rel, info.Length, info.LastWriteTimeUtc, hash);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"Bo qua {rel}: {ex.Message}");
                }
            }
        }

        return new ScanResult(entries, errors, skippedLinks);
    }

    internal static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/');
}
