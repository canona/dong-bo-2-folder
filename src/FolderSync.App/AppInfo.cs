using System.Reflection;

namespace FolderSync.App;

/// <summary>
/// Thong tin san pham / ban quyen doc tu metadata cua assembly - nguon duy nhat la Directory.Build.props
/// (khong ghi cung o day de khong lech voi Properties > Details cua file .exe).
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    public static string Product => Asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "FolderSync";

    /// <summary>Phien ban 3 so (vd 0.3.0) - bo phan "+commit" ma SDK them vao InformationalVersion.</summary>
    public static string Version
    {
        get
        {
            var v = Asm.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string Copyright => Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
                                      ?? "Copyright © 2026 VTC Digital. All rights reserved.";

    public static string Owner => Metadata("CopyrightOwner") ?? "VTC Digital";
    public static string Developer => Metadata("Developer") ?? "Canona";
    public static string DevelopedOn => Metadata("DevelopedOn") ?? "10/2026";

    /// <summary>Dong ngan cho thanh trang thai.</summary>
    public static string ShortNotice => $"© 2026 {Owner}";

    private static string? Metadata(string key)
        => Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
