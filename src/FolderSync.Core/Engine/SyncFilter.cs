using System.Text.RegularExpressions;

namespace FolderSync.Core.Engine;

/// <summary>
/// Bo loc file khi quet (Ke hoach muc 3 + tab Filters cua Job Options):
/// - Mac dinh: loai file tam Office (~$), Thumbs.db/desktop.ini/.DS_Store, file tam .tmp-sync cua app,
///   thu muc .git / node_modules / .foldersync_history (lich su cua chinh app thi TUYET DOI khong sync).
/// - Mau nguoi dung (moi mau 1 dong): * va ? la ky tu dai dien, khong phan biet hoa/thuong.
///   Mau khong co '/' khop voi TEN file hoac bat ky doan duong dan nao (vd "*.tmp", "cache").
///   Mau co '/' khop voi duong dan tuong doi day du (vd "logs/*.log", "thumuccon/*").
///   Exclude thang Include; co Include thi file phai khop it nhat 1 mau Include moi duoc lay.
/// - ExcludeHidden/ExcludeSystem: do FolderScanner ap dung qua thuoc tinh file.
/// </summary>
public sealed class SyncFilter
{
    private static readonly string[] DefaultExcludedNames = { "Thumbs.db", "desktop.ini", ".DS_Store" };
    private static readonly string[] DefaultExcludedDirs = { ".git", "node_modules", ".foldersync_history" };

    private readonly List<Regex> _include = new();
    private readonly List<Regex> _exclude = new();
    private readonly List<string> _includeBare = new();
    private readonly List<string> _excludeBare = new();

    public bool ExcludeHidden { get; }
    public bool ExcludeSystem { get; }

    public SyncFilter() : this(null, null, false, false) { }

    public SyncFilter(IEnumerable<string>? includePatterns, IEnumerable<string>? excludePatterns,
        bool excludeHidden = false, bool excludeSystem = false)
    {
        ExcludeHidden = excludeHidden;
        ExcludeSystem = excludeSystem;
        AddPatterns(includePatterns, _include, _includeBare);
        AddPatterns(excludePatterns, _exclude, _excludeBare);
    }

    private static void AddPatterns(IEnumerable<string>? patterns, List<Regex> regexes, List<string> bareNames)
    {
        if (patterns is null) return;
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Replace('\\', '/').Trim('/');
            if (p.Length == 0) continue;
            regexes.Add(GlobToRegex(p));
            if (!p.Contains('/')) bareNames.Add(p);
        }
    }

    private static Regex GlobToRegex(string pattern)
    {
        var body = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static bool MatchesAny(List<Regex> regexes, List<string> bare, string relPath, string name, string[] segments)
    {
        foreach (var re in regexes)
            if (re.IsMatch(relPath) || re.IsMatch(name)) return true;
        // Mau khong '/' con khop voi tung doan duong dan (ten thu muc o giua)
        foreach (var seg in segments)
            foreach (var b in bare)
                if (GlobToRegexCache(b).IsMatch(seg)) return true;
        return false;
    }

    private static readonly Dictionary<string, Regex> _globCache = new(StringComparer.OrdinalIgnoreCase);
    private static Regex GlobToRegexCache(string pattern)
    {
        lock (_globCache)
        {
            if (!_globCache.TryGetValue(pattern, out var re))
                _globCache[pattern] = re = GlobToRegex(pattern);
            return re;
        }
    }

    public bool IsIncluded(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts[..^1])
            if (DefaultExcludedDirs.Contains(part, StringComparer.OrdinalIgnoreCase)) return false;
        var name = parts[^1];
        if (DefaultExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return false; // file tam cua MS Office
        if (name.EndsWith(".tmp-sync", StringComparison.OrdinalIgnoreCase)) return false; // file tam cua chinh app

        var rel = string.Join('/', parts);
        if (_exclude.Count > 0 && MatchesAny(_exclude, _excludeBare, rel, name, parts)) return false;
        if (_include.Count > 0 && !MatchesAny(_include, _includeBare, rel, name, parts)) return false;
        return true;
    }
}
