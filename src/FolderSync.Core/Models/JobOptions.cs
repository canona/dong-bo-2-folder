using System.Text.Json;
using FolderSync.Core.Engine;

namespace FolderSync.Core.Models;

/// <summary>
/// Tuy chon nang cao cua 1 Job - mo hinh theo hop thoai Options cua GoodSync
/// (General / Filters / Auto / Scripts / Advanced / Left Side / Right Side).
/// Luu dang JSON trong cot jobs.settings_json. Moi truong deu co mac dinh an toan
/// de Job cu (chua co JSON) chay nhu truoc.
/// </summary>
public sealed class JobOptions
{
    // --- General ---
    /// <summary>Chi cho che do Backup: xoa o dich khi nguon da xoa (mac dinh tat cho an toan).</summary>
    public bool PropagateDeletes { get; set; }
    /// <summary>Tao folder Trai/Phai neu chua ton tai khi chay Job.</summary>
    public bool CreateFoldersIfMissing { get; set; }
    /// <summary>Tu don .foldersync_history sau so ngay nay (kem CleanupHistory).</summary>
    public bool CleanupHistory { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;

    // --- Filters (moi dong 1 mau; * va ? la ky tu dai dien) ---
    public string IncludePatterns { get; set; } = "";
    public string ExcludePatterns { get; set; } = "";
    public bool ExcludeHidden { get; set; }
    public bool ExcludeSystem { get; set; }

    // --- Auto ---
    public bool AutoOnFileChange { get; set; }
    public int AutoFileChangeDelaySec { get; set; } = 5;
    public bool AutoOnStart { get; set; }
    /// <summary>0 = tat. >0 = cu moi bao nhieu phut tu chay 1 lan.</summary>
    public int AutoPeriodMinutes { get; set; }

    // --- Scripts (lenh chay qua cmd.exe /C; bien: %JOBNAME% %RESULT% %CHANGED% %ERRORS% %CONFLICTS% %LF% %RF%) ---
    public string ScriptPreAnalyze { get; set; } = "";
    public string ScriptPostSync { get; set; } = "";

    // --- Advanced ---
    public bool CopyCreationTime { get; set; } = true;
    public bool CopyAttributes { get; set; } = true;
    public bool DetectRenames { get; set; } = true;
    /// <summary>So noi dung bang hash khi quet (tat = chi so size+mtime: nhanh hon nhung kem chac).</summary>
    public bool CompareChecksums { get; set; } = true;
    /// <summary>Nguong chot an toan xoa hang loat rieng cua Job (% so file tham chieu).</summary>
    public int SafetyMaxDeletePercent { get; set; } = 50;

    // --- Left / Right Side ---
    public int LeftToleranceSec { get; set; } = 2;
    public int RightToleranceSec { get; set; } = 2;

    // ---------- JSON ----------
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// Hanh vi cua Job tao TRUOC khi co Job Options: khong tu xoa history, khong copy
    /// creation time/thuoc tinh. Mac dinh moi (CleanupHistory=true...) chi danh cho Job moi -
    /// ap cho Job cu se am tham xoa vinh vien history qua 30 ngay ma nguoi dung khong he bat.
    /// </summary>
    public static JobOptions Legacy() => new()
    {
        CleanupHistory = false,
        CopyCreationTime = false,
        CopyAttributes = false
    };

    /// <summary>null/rong = Job moi (mac dinh moi). "{}" = gia tri mac dinh cua cot settings_json
    /// sau migration, tuc Job cu chua tung luu Options -> Legacy().</summary>
    public static JobOptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JobOptions();
        if (json.Trim() == "{}") return Legacy();
        try { return JsonSerializer.Deserialize<JobOptions>(json) ?? new JobOptions(); }
        catch (JsonException) { return new JobOptions(); }
    }

    // ---------- Mapping sang engine ----------
    public SyncFilter BuildFilter()
        => new(SplitLines(IncludePatterns), SplitLines(ExcludePatterns), ExcludeHidden, ExcludeSystem);

    public TimeSpan TimeTolerance
        => TimeSpan.FromSeconds(Math.Max(LeftToleranceSec, RightToleranceSec));

    private static string[] SplitLines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
