using System.Diagnostics;
using System.IO;

namespace FolderSync.App;

/// <summary>
/// Do thoi gian tung buoc de chan doan hieu nang. CHI ghi khi bien moi truong FOLDERSYNC_DIAG = duong dan file log
/// (nguoi dung binh thuong khong bat -> khong ghi gi, khong ton chi phi).
/// </summary>
public sealed class Diag
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("FOLDERSYNC_DIAG");
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private TimeSpan _last;

    public static Diag Start() => new();

    public void Mark(string step)
    {
        if (LogPath is null) return;
        var now = _sw.Elapsed;
        var line = $"{DateTime.Now:HH:mm:ss.fff} | {step}: {(now - _last).TotalSeconds:0.00}s (tong {now.TotalSeconds:0.00}s) | RAM {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB";
        _last = now;
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch (IOException) { }
    }
}
