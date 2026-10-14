namespace FolderSync.Core.Engine;

/// <summary>Bo loc mac dinh - Ke hoach muc 3: loai file tam Office, file he thong, thu muc dev.</summary>
public sealed class SyncFilter
{
    private static readonly string[] DefaultExcludedNames = { "Thumbs.db", "desktop.ini", ".DS_Store" };
    private static readonly string[] DefaultExcludedDirs = { ".git", "node_modules", ".foldersync_history" };

    public bool IsIncluded(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts[..^1])
            if (DefaultExcludedDirs.Contains(part, StringComparer.OrdinalIgnoreCase)) return false;
        var name = parts[^1];
        if (DefaultExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return false; // file tam cua MS Office
        if (name.EndsWith(".tmp-sync", StringComparison.OrdinalIgnoreCase)) return false; // file tam cua chinh app
        return true;
    }
}
