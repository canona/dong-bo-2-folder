using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace FolderSync.App;

/// <summary>
/// Cai dat chung cua ung dung (khong thuoc Job nao) - luu JSON canh state.db
/// (ban cai dat: %AppData%\FolderSync\app-settings.json; ban portable: canh file .exe).
/// "Chay cung Windows" KHONG luu o day: nguon su that la registry (xem StartupManager).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Bam X tren cua so: true = an xuong khay (Job lien tuc van chay), false = thoat han.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Hien thong bao o khay khi Job loi / bi huy.</summary>
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Che do Mini: ghim cua so tren cung.</summary>
    public bool MiniTopmost { get; set; } = true;
    /// <summary>Che do lan cuoi dung ("Full" / "Mini") - mo app bang tay se mo lai che do nay.</summary>
    public string LastMode { get; set; } = "Full";
    /// <summary>Da hien thong bao "FolderSync van chay o khay" lan dau chua.</summary>
    public bool TrayHintShown { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private string _path = "";

    public static AppSettings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "app-settings.json");
        AppSettings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { s = new(); } // file hong -> mac dinh, khong lam sap app
        s._path = path;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception) { /* cai dat la phu - khong lam hong thao tac chinh */ }
    }
}

/// <summary>
/// "Chay cung Windows": gia tri FolderSync trong HKCU\...\Run (theo user, khong can admin).
/// Bo cai Inno Setup ghi CUNG ten + CUNG tham so (--tray) -> bat/tat trong app va trong bo cai la mot.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "FolderSync";
    /// <summary>Tham so khi tu chay cung Windows: khoi dong an o khay, khong bat cua so.</summary>
    public const string TrayArg = "--tray";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArg}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Gia tri dang tro toi exe KHAC (vd ban cai dat cu) - bat lai se cap nhat duong dan.</summary>
    public static bool PointsToThisExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v
               && v.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
