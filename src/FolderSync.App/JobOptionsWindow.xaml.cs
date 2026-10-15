using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;
using Microsoft.Win32;

namespace FolderSync.App;

/// <summary>
/// Hop thoai tuy chon cua 1 Job - mo hinh theo Options cua GoodSync voi 7 tab:
/// Chung / Bo loc / Tu dong / Scripts / Nang cao / Phia Trai / Phia Phai.
/// Phan cot loi (folder, che do, chinh sach xung dot) nam o JobRecord;
/// phan mo rong luu JSON (JobOptions) trong jobs.settings_json.
/// </summary>
public partial class JobOptionsWindow : Window
{
    private static readonly (SyncMode Mode, string Label)[] Modes =
    {
        (SyncMode.BackupOneWay, "Backup 1 chieu (khong xoa o dich)"),
        (SyncMode.MirrorOneWay, "Mirror 1 chieu (dich giong het nguon)"),
        (SyncMode.TwoWay, "Dong bo 2 chieu")
    };

    private static readonly (ConflictPolicy Policy, string Label)[] Policies =
    {
        (ConflictPolicy.NewerWins, "Ban moi hon thang"),
        (ConflictPolicy.LeftWins, "Trai thang"),
        (ConflictPolicy.RightWins, "Phai thang"),
        (ConflictPolicy.KeepBoth, "Giu ca hai ban"),
        (ConflictPolicy.Manual, "Hoi thu cong (khong tu xu ly)")
    };

    private readonly string _existingId;

    public JobRecord? Result { get; private set; }

    public JobOptionsWindow(JobRecord? existing)
    {
        InitializeComponent();
        Icon = IconHelper.AppIcon;
        _existingId = existing?.Id ?? "job-" + Guid.NewGuid().ToString("N")[..8];
        var options = JobOptions.FromJson(existing?.SettingsJson);

        foreach (var m in Modes) ModeCombo.Items.Add(m.Label);
        foreach (var p in Policies) PolicyCombo.Items.Add(p.Label);

        NameBox.Text = existing?.Name ?? "";
        LeftBox.Text = existing?.LeftRoot ?? "";
        RightBox.Text = existing?.RightRoot ?? "";
        ModeCombo.SelectedIndex = existing is null ? 0 : Array.FindIndex(Modes, m => m.Mode == existing.Mode);
        PolicyCombo.SelectedIndex = existing is null ? 0 : Array.FindIndex(Policies, p => p.Policy == existing.Policy);

        PropagateDeletesCheck.IsChecked = options.PropagateDeletes;
        CreateFoldersCheck.IsChecked = options.CreateFoldersIfMissing;
        CleanupHistoryCheck.IsChecked = options.CleanupHistory;
        HistoryDaysBox.Text = options.HistoryRetentionDays.ToString();

        IncludeBox.Text = options.IncludePatterns;
        ExcludeBox.Text = options.ExcludePatterns;
        ExcludeHiddenCheck.IsChecked = options.ExcludeHidden;
        ExcludeSystemCheck.IsChecked = options.ExcludeSystem;

        AutoOnStartCheck.IsChecked = options.AutoOnStart;
        AutoOnFileChangeCheck.IsChecked = options.AutoOnFileChange;
        FileChangeDelayBox.Text = options.AutoFileChangeDelaySec.ToString();
        PeriodBox.Text = options.AutoPeriodMinutes.ToString();
        SafetyBox.Text = options.SafetyMaxDeletePercent.ToString();

        PreScriptBox.Text = options.ScriptPreAnalyze;
        PostScriptBox.Text = options.ScriptPostSync;

        CopyCreationTimeCheck.IsChecked = options.CopyCreationTime;
        CopyAttributesCheck.IsChecked = options.CopyAttributes;
        DetectRenamesCheck.IsChecked = options.DetectRenames;
        CompareChecksumsCheck.IsChecked = options.CompareChecksums;
        FullContentCheck.IsChecked = options.FullContentCompare;
        SetSpeedLimit(options.MaxCopySpeedMBps);

        LeftToleranceBox.Text = options.LeftToleranceSec.ToString();
        RightToleranceBox.Text = options.RightToleranceSec.ToString();
        LeftFsText.Text = FsInfo(existing?.LeftRoot);
        RightFsText.Text = FsInfo(existing?.RightRoot);
    }

    private static string FsInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "He thong file: (chua chon folder)";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return "He thong file: khong xac dinh";
            var d = new DriveInfo(root);
            return d.IsReady
                ? $"He thong file cua o {root.TrimEnd('\\')}: {d.DriveFormat} - trong {MainWindow.FormatSize(d.AvailableFreeSpace)} / tong {MainWindow.FormatSize(d.TotalSize)}"
                : $"O {root.TrimEnd('\\')}: chua san sang (USB chua cam?)";
        }
        catch { return "He thong file: khong doc duoc"; }
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ListBoxItem dau tien co IsSelected="True" nen su kien ban ngay TRONG InitializeComponent,
        // luc cac panel khai bao phia duoi chua duoc tao (null) -> crash khi mo hop Them/Sua Job.
        // XAML da de san PanelGeneral hien, cac panel khac an - dung trang thai tab 0.
        if (PanelRight is null) return;
        var panels = new[] { PanelGeneral, PanelFilters, PanelAuto, PanelScripts, PanelAdvanced, PanelLeft, PanelRight };
        for (int i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == TabList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        if (TabList.SelectedIndex == 5) LeftFsText.Text = FsInfo(LeftBox.Text);
        if (TabList.SelectedIndex == 6) RightFsText.Text = FsInfo(RightBox.Text);
    }

    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftBox);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightBox);

    private static void Browse(TextBox target)
    {
        var dlg = new OpenFolderDialog { Title = "Chon folder" };
        if (Directory.Exists(target.Text)) dlg.InitialDirectory = target.Text;
        if (dlg.ShowDialog() == true) target.Text = dlg.FolderName;
    }

    // ---------- Gioi han toc do chep ----------

    private const int MaxSpeedMBps = 10_000;
    private int _speedLimit;
    private bool _syncingSpeed; // dang dong bo slider <-> o so: chan vong lap su kien

    private static int ParseSpeed(string text, int fallback)
        => int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, 0, MaxSpeedMBps) : fallback;

    /// <summary>Gia tri goc la _speedLimit; slider (toi da 200) va o so (toi da 10.000) chi la 2 cach nhap.</summary>
    private void SetSpeedLimit(int mbps)
    {
        _speedLimit = Math.Clamp(mbps, 0, MaxSpeedMBps);
        _syncingSpeed = true;
        try
        {
            SpeedLimitSlider.Value = Math.Min(_speedLimit, SpeedLimitSlider.Maximum);
            SpeedLimitBox.Text = _speedLimit.ToString();
        }
        finally { _syncingSpeed = false; }
        SpeedLimitHint.Text = _speedLimit == 0
            ? "Khong gioi han - chep nhanh nhat co the."
            : $"Toi da {_speedLimit} MB/s cho Job nay (tinh chung moi file). Vi du 1 GB mat khoang {FormatDuration(1024.0 / _speedLimit)}.";
    }

    private static string FormatDuration(double seconds)
        => seconds < 60 ? $"{seconds:0} giay" : $"{(int)(seconds / 60)} phut {(int)(seconds % 60)} giay";

    private void SpeedLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Ban trong InitializeComponent (Minimum/Maximum) khi o so va dong goi y chua duoc tao
        if (_syncingSpeed || SpeedLimitBox is null || SpeedLimitHint is null) return;
        SetSpeedLimit((int)Math.Round(e.NewValue));
    }

    private void SpeedLimitBox_LostFocus(object sender, RoutedEventArgs e)
        => SetSpeedLimit(ParseSpeed(SpeedLimitBox.Text, _speedLimit));

    private void SpeedLimitBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SetSpeedLimit(ParseSpeed(SpeedLimitBox.Text, _speedLimit));
    }

    private static int ParseInt(TextBox box, int fallback)
        => int.TryParse(box.Text.Trim(), out var v) && v >= 0 ? v : fallback;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var left = LeftBox.Text.Trim();
        var right = RightBox.Text.Trim();
        if (name.Length == 0) { ValidationText.Text = "Chua nhap ten Job."; return; }
        if (left.Length == 0 || right.Length == 0) { ValidationText.Text = "Chua chon du 2 folder."; return; }
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
        { ValidationText.Text = "Folder Trai va Phai phai khac nhau."; return; }

        var options = new JobOptions
        {
            PropagateDeletes = PropagateDeletesCheck.IsChecked == true,
            CreateFoldersIfMissing = CreateFoldersCheck.IsChecked == true,
            CleanupHistory = CleanupHistoryCheck.IsChecked == true,
            HistoryRetentionDays = ParseInt(HistoryDaysBox, 30),
            IncludePatterns = IncludeBox.Text.Trim(),
            ExcludePatterns = ExcludeBox.Text.Trim(),
            ExcludeHidden = ExcludeHiddenCheck.IsChecked == true,
            ExcludeSystem = ExcludeSystemCheck.IsChecked == true,
            AutoOnStart = AutoOnStartCheck.IsChecked == true,
            AutoOnFileChange = AutoOnFileChangeCheck.IsChecked == true,
            AutoFileChangeDelaySec = Math.Max(1, ParseInt(FileChangeDelayBox, 5)),
            AutoPeriodMinutes = ParseInt(PeriodBox, 0),
            SafetyMaxDeletePercent = Math.Clamp(ParseInt(SafetyBox, 50), 1, 100),
            ScriptPreAnalyze = PreScriptBox.Text.Trim(),
            ScriptPostSync = PostScriptBox.Text.Trim(),
            CopyCreationTime = CopyCreationTimeCheck.IsChecked == true,
            CopyAttributes = CopyAttributesCheck.IsChecked == true,
            DetectRenames = DetectRenamesCheck.IsChecked == true,
            CompareChecksums = CompareChecksumsCheck.IsChecked == true,
            FullContentCompare = FullContentCheck.IsChecked == true,
            // Doc lai o so: bam Luu ngay sau khi go (chua roi o) van lay dung gia tri
            MaxCopySpeedMBps = ParseSpeed(SpeedLimitBox.Text, _speedLimit),
            LeftToleranceSec = ParseInt(LeftToleranceBox, 2),
            RightToleranceSec = ParseInt(RightToleranceBox, 2)
        };

        Result = new JobRecord(_existingId, name, left, right,
            Modes[Math.Max(0, ModeCombo.SelectedIndex)].Mode,
            Policies[Math.Max(0, PolicyCombo.SelectedIndex)].Policy,
            options.ToJson());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
