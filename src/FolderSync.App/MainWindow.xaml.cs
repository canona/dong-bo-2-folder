using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.App;

/// <summary>1 dong trong danh sach Job: ten + trang thai chay/tu dong (cap nhat tu JobRunner).</summary>
public sealed class JobListItem : INotifyPropertyChanged
{
    private static readonly Brush Muted = Frozen(0x64, 0x74, 0x8B);
    private static readonly Brush Busy = Frozen(0x08, 0x91, 0xB2);
    private static readonly Brush Ok = Frozen(0x16, 0xA3, 0x4A);
    private static readonly Brush Bad = Frozen(0xDC, 0x26, 0x26);

    public JobListItem(JobRecord job) => Job = job;

    public JobRecord Job { get; set; }
    public string Name => Job.Name;
    public string StatusLine { get; private set; } = "Chua chay";
    public Brush StatusBrush { get; private set; } = Muted;
    public string AutoText { get; private set; } = "";
    public Visibility AutoVisibility => AutoText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Tien trinh rut gon cho che do Mini (MainWindow cap nhat tu JobView cua Job)
    public double ProgressValue { get; private set; }
    public string ProgressText { get; private set; } = "";
    public Brush ProgressBrush { get; private set; } = Ok;
    public Visibility ProgressVisibility { get; private set; } = Visibility.Collapsed;
    public bool CanSync { get; private set; } = true;

    public void SetProgress(double fraction, string text, bool hasErrors)
    {
        ProgressValue = fraction * 1000;
        ProgressText = text;
        ProgressBrush = hasErrors ? Bad : Ok;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Update(JobRunner? runner)
    {
        CanSync = runner is { IsBusy: false };
        ProgressVisibility = runner?.State == JobRunState.Running ? Visibility.Visible : Visibility.Collapsed;
        var o = runner?.Options ?? JobOptions.FromJson(Job.SettingsJson);
        AutoText = o.AutoOnFileChange ? "LIEN TUC" : o.AutoPeriodMinutes > 0 ? $"{o.AutoPeriodMinutes} PHUT" : "";
        var last = runner?.LastFinishedLocal is { } t ? $"{t:HH:mm} {runner.LastSummary}" : null;
        (StatusLine, StatusBrush) = runner?.State switch
        {
            JobRunState.Running => ("Dang dong bo...", Busy),
            JobRunState.Analyzing => ("Dang phan tich...", Busy),
            JobRunState.Watching when runner.LastErrorCount > 0 => ("⚠ Dang theo doi • " + last, Bad),
            JobRunState.Watching => ("Dang theo doi" + (last is null ? "" : " • " + last), Ok),
            _ when runner?.LastErrorCount > 0 => ("⚠ " + last, Bad),
            _ => (last ?? "Chua chay", Muted)
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Du lieu hien thi RIENG cua tung Job (bang phan tich + tien trinh) - chuyen qua lai giua cac Job
/// khong mat ket qua, va Job chay nen van cap nhat du lieu cua no du dang xem Job khac.
/// </summary>
public sealed class JobView
{
    public List<ActionRow> Rows { get; set; } = new();
    public string Note { get; set; } = "Bam Phan tich de xem truoc cac thay doi.";

    // Tien trinh (chi doc/ghi tren UI thread)
    public int FilesDone, FilesTotal;
    public long BytesTotal, BytesProcessed, BytesTransferred, CurDone, CurTotal;
    public readonly Stopwatch Watch = new();
    public readonly Queue<(double Sec, long Bytes)> SpeedSamples = new();
    public bool ProgressVisible;
    /// <summary>Dang quet: chua biet tong so file -> thanh tien trinh chay vo han.</summary>
    public bool Indeterminate;
    /// <summary>Gioi han toc do cua lan chay (MB/s, 0 = khong) - hien canh toc do de nguoi dung hieu vi sao cham.</summary>
    public int SpeedLimitMBps;
    public int ErrorCount;
    public double ProgressFraction;
    public string ProgressLeft = "", ProgressRight = "", CurrentFile = "";
}

public partial class MainWindow : Window
{
    private readonly SqliteStateStore _store;
    private readonly ObservableCollection<JobListItem> _jobs = new();
    private readonly Dictionary<string, JobRunner> _runners = new();
    private readonly Dictionary<string, JobView> _views = new();
    private readonly Dictionary<ActionRow, CompareNode> _nodeByRow = new();
    /// <summary>Cac dong DANG HIEN cua cay (thu muc mo -> co ca con) - nguon cua ListBox ao hoa.</summary>
    private readonly BulkObservableCollection<CompareNode> _flat = new();
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    // Mau canh bao dung chung (thanh dung luong, tien trinh, chip, log)
    private static readonly Brush ErrorBrush = Frozen(0xDC, 0x26, 0x26);
    private static readonly Brush ErrorBgBrush = Frozen(0xFE, 0xF2, 0xF2);
    private static readonly Brush WarnBrush = Frozen(0xD9, 0x77, 0x06);
    private static readonly Brush OkBrush = Frozen(0x16, 0xA3, 0x4A);
    private static readonly Brush UsedBrush = Frozen(0x3B, 0x82, 0xF6);
    private static readonly Brush NeedBrush = Frozen(0xF5, 0x9E, 0x0B);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Dung luong o cua 2 folder (do lai moi khi ve header) - null = o khong san sang.</summary>
    private sealed record DiskInfo(string Label, long Free, long Total);
    private DiskInfo? _leftDisk, _rightDisk;

    // Che do chay: Full (cua so nay) / Mini (MiniWindow) / chi o khay (TrayIconService)
    private readonly AppSettings _settings;
    private readonly TrayIconService _tray;
    private MiniWindow? _mini;
    private bool _initialized, _autoPaused;
    /// <summary>Dang thoat that su (menu Thoat) - Closing khong duoc an xuong khay nua.</summary>
    public bool IsExiting { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Icon = IconHelper.AppIcon;
        AppIconImage.Source = IconHelper.AppIcon;
        var dbPath = ResolveDbPath();
        _store = new SqliteStateStore(dbPath);
        _settings = AppSettings.Load(Path.GetDirectoryName(dbPath)!);
        JobList.ItemsSource = _jobs;
        CompareList.ItemsSource = _flat;
        _tray = new TrayIconService
        {
            OpenFull = ShowFull,
            OpenMini = ShowMini,
            SyncAll = () => _ = SyncAllAsync(),
            TogglePause = TogglePauseAuto,
            ToggleStartup = ToggleStartWithWindows,
            Exit = ExitApp
        };
        Closing += MainWindow_Closing;
        // Cay luon hien thanh cuon doc -> tieu de chua cho dung bang do de cac cot thang hang
        TreeHeaderGrid.Margin = new Thickness(0, 0, SystemParameters.VerticalScrollBarWidth, 0);
        Closed += (_, _) =>
        {
            foreach (var r in _runners.Values) r.Dispose(); // dung watcher/timer cua moi Job
        };
    }

    // Ban portable: neu co file portable.flag canh file .exe thi state nam ngay trong thu muc app.
    // Ban cai dat: state nam o %AppData%\FolderSync\state.db.
    private static string ResolveDbPath()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.flag")))
            return Path.Combine(exeDir, "state.db");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "FolderSync", "state.db");
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => InitOnce();

    /// <summary>
    /// Nap Job + khoi dong tu dong 1 lan duy nhat. Goi tu Start() chu khong chi Window_Loaded:
    /// khoi dong o khay (--tray, chay cung Windows) thi cua so chua bao gio hien -> Loaded khong ban,
    /// neu chi dua vao Loaded thi cac Job lien tuc se khong chay.
    /// </summary>
    private void InitOnce()
    {
        if (_initialized) return;
        _initialized = true;
        ReloadJobs(appStarting: true);
        UpdateTrayStatus();
    }

    /// <summary>Goi tu App.OnStartup.</summary>
    public void Start(StartMode mode)
    {
        InitOnce();
        switch (mode)
        {
            case StartMode.Tray:
                break; // chi icon khay, Job lien tuc van chay
            case StartMode.Mini:
                ShowMini();
                break;
            default:
                if (_settings.LastMode == "Mini") ShowMini(); else ShowFull();
                break;
        }
    }

    public void ShowFull()
    {
        _mini?.Hide();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _settings.LastMode = "Full";
        _settings.Save();
    }

    public void ShowMini()
    {
        Hide();
        _mini ??= new MiniWindow(this, _jobs, _settings.MiniTopmost);
        _mini.SetSummary(TrayStatusText());
        _mini.Show();
        if (_mini.WindowState == WindowState.Minimized) _mini.WindowState = WindowState.Normal;
        _mini.Activate();
        _settings.LastMode = "Mini";
        _settings.Save();
    }

    public void HideMini()
    {
        _mini?.Hide();
        ShowTrayHintOnce();
    }

    public void SetMiniTopmost(bool topmost)
    {
        _settings.MiniTopmost = topmost;
        _settings.Save();
    }

    private void ShowTrayHintOnce()
    {
        if (_settings.TrayHintShown) return;
        _tray.Notify("FolderSync van dang chay",
            "Cac Job tu dong van chay nen. Nhap dup icon o khay de mo lai, chuot phai de Thoat.", error: false);
        _settings.TrayHintShown = true;
        _settings.Save();
    }

    /// <summary>Bam X: an xuong khay (mac dinh) hoac thoat han tuy cai dat.</summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (IsExiting) return;
        e.Cancel = true;
        if (_settings.CloseToTray)
        {
            Hide();
            ShowTrayHintOnce();
        }
        else
        {
            ExitApp();
        }
    }

    /// <summary>Thoat that su: dung moi Job, go icon khay, dong moi cua so.</summary>
    public void ExitApp()
    {
        var busy = _runners.Values.Where(r => r.IsBusy).Select(r => r.Job.Name).ToList();
        if (busy.Count > 0 && MessageBox.Show(
                $"Dang dong bo: {string.Join(", ", busy)}.\nThoat bay gio se dung giua chung (file dang chep do bi bo, file dich khong bi hong; lan sau se chep lai).\nVan thoat?",
                "Thoat FolderSync", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsExiting = true;
        foreach (var r in _runners.Values) { r.Cancel(); r.Dispose(); } // huy -> file dang chep do duoc xoa
        _tray.Dispose();
        _mini?.Close();
        Close();
        Application.Current.Shutdown();
    }

    // ---------- Thao tac nhanh tu khay / Mini ----------

    /// <summary>Dong bo ngay moi Job dang ranh - chay song song, doc lap.</summary>
    public async Task SyncAllAsync()
    {
        var free = _runners.Values.Where(r => !r.IsBusy).ToList();
        if (free.Count == 0) { _tray.Notify("FolderSync", "Moi Job dang chay.", error: false); return; }
        if (MessageBox.Show($"Dong bo ngay {free.Count} Job: {string.Join(", ", free.Select(r => r.Job.Name))}?\n" +
                            "File bi xoa/ghi de se duoc dua vao .foldersync_history de khoi phuc.",
                "Dong bo tat ca", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        Log($"Dong bo tat ca: {free.Count} Job.");
        await Task.WhenAll(free.Select(r => r.RunAsync("thu cong", r.AutoProgress)));
    }

    public async Task SyncJobFromShortcutAsync(string jobId)
    {
        if (!_runners.TryGetValue(jobId, out var runner)) return;
        if (runner.IsBusy) { Log($"Job '{runner.Job.Name}' dang chay - thu lai sau."); return; }
        if (MessageBox.Show($"Dong bo Job '{runner.Job.Name}' ngay bay gio?", "Dong bo",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (await runner.RunAsync("thu cong", runner.AutoProgress) is null)
            Log($"Job '{runner.Job.Name}' dang chay - bo qua yeu cau dong bo.");
    }

    /// <summary>Tam dung / tiep tuc moi tu dong (lien tuc + dinh ky) cua tat ca Job - vd khi can rut USB.</summary>
    private void TogglePauseAuto()
    {
        _autoPaused = !_autoPaused;
        foreach (var r in _runners.Values)
        {
            if (_autoPaused) r.StopAuto(); else r.StartAuto(appStarting: false);
        }
        _tray.SetPaused(_autoPaused);
        Log(_autoPaused ? "Da TAM DUNG tu dong dong bo cho moi Job." : "Da TIEP TUC tu dong dong bo.");
        UpdateTrayStatus();
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            bool enable = !StartupManager.IsEnabled() || !StartupManager.PointsToThisExe();
            StartupManager.SetEnabled(enable);
            Log(enable ? "Da BAT chay cung Windows (khoi dong an o khay)." : "Da TAT chay cung Windows.");
        }
        catch (Exception ex)
        {
            Log("LOI khi doi 'Chay cung Windows': " + ex.Message);
        }
    }

    private string TrayStatusText()
    {
        int running = _runners.Values.Count(r => r.State == JobRunState.Running);
        int auto = _runners.Values.Count(r => r.State == JobRunState.Watching || (r.AutoEnabled && r.IsBusy));
        int errors = _runners.Values.Count(r => r.LastErrorCount > 0);
        var parts = new List<string> { $"{_runners.Count} Job" };
        if (_autoPaused) parts.Add("TAM DUNG tu dong");
        else if (auto > 0) parts.Add($"{auto} lien tuc");
        if (running > 0) parts.Add($"dang dong bo {running}");
        if (errors > 0) parts.Add($"{errors} Job loi");
        return string.Join(" • ", parts);
    }

    private void UpdateTrayStatus()
    {
        var text = TrayStatusText();
        _tray.SetStatus(text);
        _mini?.SetSummary(text);
    }

    /// <summary>Thong bao o khay khi nguoi dung KHONG nhin thay cua so chinh (an / thu nho / dang o Mini).</summary>
    private void NotifyIfHidden(string title, string message)
    {
        if (!_settings.ShowNotifications) return;
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) return;
        _tray.Notify(title, message, error: true);
    }

    // ---------- Menu Cai dat tren thanh cong cu ----------

    private void Mini_Click(object sender, RoutedEventArgs e) => ShowMini();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Check(string header, bool isChecked, Action toggle)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) => toggle();
            return item;
        }
        menu.Items.Add(Check("Chay cung Windows (an o khay)", StartupManager.IsEnabled() && StartupManager.PointsToThisExe(),
            ToggleStartWithWindows));
        menu.Items.Add(Check("Bam X: an xuong khay (khong thoat)", _settings.CloseToTray, () =>
        {
            _settings.CloseToTray = !_settings.CloseToTray;
            _settings.Save();
        }));
        menu.Items.Add(Check("Thong bao o khay khi Job loi", _settings.ShowNotifications, () =>
        {
            _settings.ShowNotifications = !_settings.ShowNotifications;
            _settings.Save();
        }));
        menu.Items.Add(Check("Tam dung tu dong dong bo (moi Job)", _autoPaused, TogglePauseAuto));
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Thoat FolderSync" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    // ---------- Danh sach Job + JobRunner (moi Job 1 runner doc lap) ----------

    /// <summary>
    /// Doc lai Job tu DB va dong bo danh sach runner: Job khong doi giu nguyen runner (dang chay thi
    /// chay tiep), Job bi sua thi thay runner moi (khoa chay theo Id nen khong chay chong), Job bi xoa
    /// thi dung runner.
    /// </summary>
    private void ReloadJobs(bool appStarting = false, string? selectId = null)
    {
        selectId ??= SelectedJob?.Id;
        var jobs = _store.ListJobs();

        foreach (var id in _runners.Keys.Except(jobs.Select(j => j.Id)).ToList())
        {
            _runners[id].Dispose();
            _runners.Remove(id);
            _views.Remove(id);
        }

        foreach (var job in jobs)
        {
            if (_runners.TryGetValue(job.Id, out var old) && old.Job == job) continue;
            if (old is not null)
            {
                // Doi folder -> ket qua phan tich cu khong con dung
                // Doi folder hoac che do -> ket qua phan tich cu (tinh theo cau hinh cu) khong con dung
                if (old.Job.LeftRoot != job.LeftRoot || old.Job.RightRoot != job.RightRoot || old.Job.Mode != job.Mode)
                    _views.Remove(job.Id);
                old.Dispose();
            }
            var runner = new JobRunner(job, _store);
            if (old is not null) runner.InheritHistory(old);
            runner.AutoProgress = new Progress<SyncProgress>(p => OnSyncProgress(job.Id, p));
            runner.Log += (r, msg) => Dispatcher.BeginInvoke(() => Log(msg));
            runner.StateChanged += r => Dispatcher.BeginInvoke(() => OnRunnerStateChanged(r));
            runner.Started += (r, trigger) => Dispatcher.BeginInvoke(() => OnRunStarted(r, trigger));
            runner.Finished += (r, report) => Dispatcher.BeginInvoke(() => OnRunFinished(r, report));
            _runners[job.Id] = runner;
            if (!_autoPaused) runner.StartAuto(appStarting);
        }

        _jobs.Clear();
        foreach (var job in jobs)
        {
            var item = new JobListItem(job);
            item.Update(_runners[job.Id]);
            _jobs.Add(item);
        }
        JobList.SelectedItem = _jobs.FirstOrDefault(j => j.Job.Id == selectId) ?? _jobs.FirstOrDefault();
    }

    private JobRecord? SelectedJob => (JobList.SelectedItem as JobListItem)?.Job;
    private JobRunner? SelectedRunner => SelectedJob is { } j && _runners.TryGetValue(j.Id, out var r) ? r : null;
    private JobView? SelectedView => SelectedJob is { } j ? ViewOf(j.Id) : null;
    private JobView ViewOf(string jobId) => _views.TryGetValue(jobId, out var v) ? v : _views[jobId] = new JobView();
    private bool IsSelected(string jobId) => SelectedJob?.Id == jobId;
    private JobRecord? JobOf(string jobId) => _runners.TryGetValue(jobId, out var r) ? r.Job : null;

    /// <summary>Cac dong cua Job dang xem (bang phan tich / dong bo gan nhat).</summary>
    private List<ActionRow> _rows => SelectedView?.Rows ?? new List<ActionRow>();

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null)
        {
            LeftPathText.Text = "(chua chon Job)";
            RightPathText.Text = "(chua chon Job)";
            LeftDriveText.Text = RightDriveText.Text = LeftRequiredText.Text = RightRequiredText.Text = "";
            _leftDisk = _rightDisk = null;
            UpdateRequiredText();
            ModeArrowText.Text = "⇄";
            ModeNameText.Text = "";
            SummaryText.Text = "";
            _flat.ReplaceAll(Array.Empty<CompareNode>());
            JobProgressPanel.Visibility = Visibility.Collapsed;
            RefreshChips();
            UpdateButtons();
            return;
        }
        UpdateDriveHeader(job);
        RenderSelectedView();
        UpdateButtons();
    }

    /// <summary>Ve lai bang + tien trinh tu du lieu da luu cua Job dang chon.</summary>
    private void RenderSelectedView()
    {
        var view = SelectedView;
        if (view is null) return;
        UpdateRequiredText();
        RefreshChips();
        RebuildTree();
        if (view.Rows.Count == 0) SummaryText.Text = view.Note;
        ApplyProgressToUi(view);
    }

    private void UpdateButtons()
    {
        var runner = SelectedRunner;
        bool free = runner is { IsBusy: false };
        AnalyzeButton.IsEnabled = free;
        SyncButton.IsEnabled = free;
        CancelButton.Visibility = runner is { IsBusy: true } ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        HistoryButton.IsEnabled = runner is not null;
        StatusText.Text = runner is null ? "San sang."
            : runner.State switch
            {
                JobRunState.Running => $"Job '{runner.Job.Name}' dang dong bo...",
                JobRunState.Analyzing => $"Job '{runner.Job.Name}' dang phan tich...",
                JobRunState.Watching => $"Job '{runner.Job.Name}': dong bo lien tuc dang bat - tu chay khi co thay doi.",
                _ => "San sang."
            };
    }

    private void UpdateDriveHeader(JobRecord job)
    {
        LeftPathText.Text = job.LeftRoot;
        RightPathText.Text = job.RightRoot;
        _leftDisk = DiskOf(job.LeftRoot);
        _rightDisk = DiskOf(job.RightRoot);
        UpdateRequiredText();
        ModeArrowText.Text = job.Mode == SyncMode.TwoWay ? "⇄" : "→";
        ModeNameText.Text = job.Mode switch
        {
            SyncMode.TwoWay => "Dong bo 2 chieu",
            SyncMode.MirrorOneWay => "Mirror 1 chieu",
            _ => "Backup 1 chieu"
        };
    }

    private static DiskInfo? DiskOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var d = new DriveInfo(root);
            if (!d.IsReady) return new DiskInfo($"{root.TrimEnd('\\')}: o chua san sang", -1, -1);
            return new DiskInfo($"{root.TrimEnd('\\')} {d.DriveFormat}", d.AvailableFreeSpace, d.TotalSize);
        }
        catch { return null; }
    }

    // ---------- Dong hanh dong ----------

    private static (string Text, long? Bytes, DateTime? When) Probe(string root, string rel)
    {
        try
        {
            var fi = new FileInfo(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!fi.Exists) return ("(khong co)", null, null);
            return (FormatSize(fi.Length), fi.Length, fi.LastWriteTime);
        }
        catch { return ("(khong co)", null, null); }
    }

    private static string FormatWhen(DateTime? when) => when?.ToString("dd/MM/yyyy HH:mm") ?? "";

    /// <summary>
    /// Tao dong bang. Co ket qua quet (left/right) thi lay size/ngay tu do - KHONG doc lai file: ban cu goi
    /// FileInfo 2 lan moi dong tren UI thread (40.000 file = 2,8 s dung hinh, USB con cham hon).
    /// </summary>
    private static List<ActionRow> BuildRows(JobRecord job, IEnumerable<SyncAction> actions,
        IReadOnlyDictionary<string, FileEntry>? left = null, IReadOnlyDictionary<string, FileEntry>? right = null)
        => actions.Select(a =>
        {
            var (lText, lBytes, lWhen) = left is null ? Probe(job.LeftRoot, a.RelativePath) : FromScan(left, a.RelativePath);
            var (rText, rBytes, rWhen) = right is null ? Probe(job.RightRoot, a.RelativePath) : FromScan(right, a.RelativePath);
            return new ActionRow(KindName(a.Kind), a.RelativePath, a.TargetPath ?? "", a.Reason)
            {
                Kind = a.Kind,
                LSizeText = lText, RSizeText = rText,
                LBytes = lBytes, RBytes = rBytes,
                LWhen = lWhen, RWhen = rWhen,
                LDateText = FormatWhen(lWhen), RDateText = FormatWhen(rWhen),
                DirectionText = DirectionOf(a.Kind)
            };
        }).ToList();

    private static (string Text, long? Bytes, DateTime? When) FromScan(IReadOnlyDictionary<string, FileEntry> scan, string rel)
        => scan.TryGetValue(rel, out var e)
            ? (FormatSize(e.Size), e.Size, e.MtimeUtc.ToLocalTime())
            : ("(khong co)", null, null);

    /// <summary>Gan bang moi cho 1 Job; Job dang xem thi ve lai ngay.</summary>
    private void SetRows(string jobId, IEnumerable<SyncAction> actions, string note,
        IReadOnlyDictionary<string, FileEntry>? left = null, IReadOnlyDictionary<string, FileEntry>? right = null)
    {
        var job = JobOf(jobId);
        if (job is null) return;
        var view = ViewOf(jobId);
        view.Rows = BuildRows(job, actions, left, right);
        view.Note = note;
        if (IsSelected(jobId)) RenderSelectedView();
    }

    /// <summary>Dung luong con phai chep sang tung phia = cac dong copy chua xu ly xong.</summary>
    private (long ToLeft, long ToRight) RequiredBytes()
    {
        long toLeft = 0, toRight = 0;
        foreach (var r in _rows.Where(r => !r.Synced))
        {
            if (r.Kind == SyncActionKind.CopyLeftToRight) toRight += r.LBytes ?? 0;
            else if (r.Kind == SyncActionKind.CopyRightToLeft) toLeft += r.RBytes ?? 0;
        }
        return (toLeft, toRight);
    }

    /// <summary>Ve thanh dung luong + dong "Can chep" / canh bao "Khong du cho" cho ca 2 the folder.</summary>
    private void UpdateRequiredText()
    {
        var (toLeft, toRight) = RequiredBytes();
        ApplyDisk(_leftDisk, toLeft, LeftDriveText, LeftRequiredText, LeftUsedCol, LeftNeedCol, LeftFreeCol, LeftUsedFill, LeftNeedFill);
        ApplyDisk(_rightDisk, toRight, RightDriveText, RightRequiredText, RightUsedCol, RightNeedCol, RightFreeCol, RightUsedFill, RightNeedFill);
    }

    private static void ApplyDisk(DiskInfo? disk, long need, TextBlock driveText, TextBlock requiredText,
        ColumnDefinition usedCol, ColumnDefinition needCol, ColumnDefinition freeCol, Border usedFill, Border needFill)
    {
        if (disk is null || disk.Total <= 0)
        {
            driveText.Text = disk?.Label ?? "";
            driveText.Foreground = disk is null ? Brushes.Gray : ErrorBrush;
            usedCol.Width = new GridLength(0, GridUnitType.Star);
            needCol.Width = new GridLength(0, GridUnitType.Star);
            freeCol.Width = new GridLength(1, GridUnitType.Star);
            requiredText.Text = need > 0 ? $"Can chep: {FormatSize(need)}" : "";
            requiredText.Foreground = OkBrush;
            return;
        }

        long used = disk.Total - disk.Free;
        bool fits = need <= disk.Free;
        long needShown = Math.Min(need, disk.Free);
        double usedPct = used * 100.0 / disk.Total;
        usedCol.Width = new GridLength(used, GridUnitType.Star);
        needCol.Width = new GridLength(needShown, GridUnitType.Star);
        freeCol.Width = new GridLength(disk.Free - needShown, GridUnitType.Star);
        // O gan day: xanh -> cam (>80%) -> do (>95%)
        usedFill.Background = usedPct > 95 ? ErrorBrush : usedPct > 80 ? WarnBrush : UsedBrush;
        needFill.Background = fits ? NeedBrush : ErrorBrush;

        driveText.Text = $"{disk.Label} • Da dung {usedPct:0}% • Trong: {FormatSize(disk.Free)} / {FormatSize(disk.Total)}";
        driveText.Foreground = usedPct > 95 ? ErrorBrush : Brushes.SlateGray;
        if (need <= 0) { requiredText.Text = ""; return; }
        requiredText.Text = fits
            ? $"Can chep: {FormatSize(need)} (con lai sau khi chep: {FormatSize(disk.Free - need)})"
            : $"⚠ Khong du cho: can chep {FormatSize(need)}, o chi con {FormatSize(disk.Free)} - thieu {FormatSize(need - disk.Free)}";
        requiredText.Foreground = fits ? OkBrush : ErrorBrush;
    }

    /// <summary>
    /// Doc lai file that o 2 phia sau khi 1 hanh dong xu ly xong - cay hien dung thuc te
    /// (copy: phia dich co size/ngay; xoa: "(khong co)"...), khong phai so lieu cu luc Phan tich.
    /// </summary>
    private static void RefreshRowFromDisk(JobRecord job, ActionRow row, bool ok)
        => row.ApplySnapshot(Probe(job.LeftRoot, row.DuongDan), Probe(job.RightRoot, row.DuongDan), ok, FormatWhen);

    private static string DoneText(SyncAction a, string? destination) => a.Kind switch
    {
        SyncActionKind.CopyLeftToRight => a.TargetPath is null ? "Da chep sang Phai" : "Da tao ban conflict o Phai",
        SyncActionKind.CopyRightToLeft => a.TargetPath is null ? "Da chep sang Trai" : "Da tao ban conflict o Trai",
        SyncActionKind.DeleteLeft => destination is null ? "Da xoa o Trai" : "Da xoa o Trai (ban cu trong history)",
        SyncActionKind.DeleteRight => destination is null ? "Da xoa o Phai" : "Da xoa o Phai (ban cu trong history)",
        SyncActionKind.MoveLeft => "Da doi ten o Trai",
        SyncActionKind.MoveRight => "Da doi ten o Phai",
        _ => "Xong"
    };

    private static string DirectionOf(SyncActionKind kind) => kind switch
    {
        SyncActionKind.CopyLeftToRight => "→",
        SyncActionKind.CopyRightToLeft => "←",
        SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight => "×",
        SyncActionKind.MoveLeft or SyncActionKind.MoveRight => "↔",
        SyncActionKind.Conflict => "⚠",
        _ => "—"
    };

    // ---------- Loc + cay ----------

    private enum ViewFilter { All, Changes, Conflicts, Deletes, Errors }

    private ViewFilter CurrentFilter =>
        FilterChanges.IsChecked == true ? ViewFilter.Changes :
        FilterConflicts.IsChecked == true ? ViewFilter.Conflicts :
        FilterDeletes.IsChecked == true ? ViewFilter.Deletes :
        FilterErrors.IsChecked == true ? ViewFilter.Errors : ViewFilter.All;

    private bool PassesFilter(ActionRow r) => CurrentFilter switch
    {
        ViewFilter.Changes => r.Kind != SyncActionKind.Skip,
        ViewFilter.Conflicts => r.Kind == SyncActionKind.Conflict,
        ViewFilter.Deletes => r.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight,
        ViewFilter.Errors => r.TrangThai == "LOI",
        _ => true
    };

    private void RefreshChips()
    {
        var rows = _rows;
        FilterAll.Content = $"Tat ca ({rows.Count})";
        FilterChanges.Content = $"Thay doi ({rows.Count(r => r.Kind != SyncActionKind.Skip)})";
        FilterConflicts.Content = $"Xung dot ({rows.Count(r => r.Kind == SyncActionKind.Conflict)})";
        FilterDeletes.Content = $"Xoa ({rows.Count(r => r.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight)})";
        int errors = rows.Count(r => r.TrangThai == "LOI");
        FilterErrors.Content = $"Loi ({errors})";
        if (errors > 0)
        {
            FilterErrors.Foreground = ErrorBrush;
            FilterErrors.FontWeight = FontWeights.Bold;
        }
        else
        {
            FilterErrors.ClearValue(ForegroundProperty);
            FilterErrors.ClearValue(FontWeightProperty);
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RebuildTree();
    }

    private void RebuildTree()
    {
        var rows = _rows;
        var filtered = rows.Where(PassesFilter).OrderBy(r => r.DuongDan, StringComparer.OrdinalIgnoreCase).ToList();
        var roots = new List<CompareNode>();
        var folders = new Dictionary<string, CompareNode>(StringComparer.OrdinalIgnoreCase);
        _nodeByRow.Clear();

        foreach (var row in filtered)
        {
            var parts = row.DuongDan.Split('/');
            CompareNode? parent = null;
            string acc = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                acc = acc.Length == 0 ? parts[i] : acc + "/" + parts[i];
                if (!folders.TryGetValue(acc, out var folder))
                {
                    folder = new CompareNode { Name = parts[i], IsFolder = true, Level = i };
                    folders[acc] = folder;
                    if (parent is null) roots.Add(folder); else parent.Children.Add(folder);
                }
                parent = folder;
            }
            var leaf = new CompareNode(row) { Name = parts[^1], Level = parts.Length - 1 };
            if (parent is null) roots.Add(leaf); else parent.Children.Add(leaf);
            _nodeByRow[row] = leaf;
        }

        foreach (var root in roots) Aggregate(root);
        var flat = new List<CompareNode>(filtered.Count + folders.Count);
        foreach (var root in roots) AppendVisible(root, flat);
        _flat.ReplaceAll(flat);
        var note = SelectedView?.Note;
        SummaryText.Text = rows.Count == 0 ? note ?? "" : $"{note}  •  Dang hien {filtered.Count} / {rows.Count} muc";
    }

    /// <summary>Them node + (neu dang mo) moi con chau dang hien, dung thu tu cay.</summary>
    private void AppendVisible(CompareNode node, List<CompareNode> output)
    {
        output.Add(node);
        node.ExpandedChanged = OnNodeExpandedChanged;
        if (!node.IsFolder || !node.IsExpanded) return;
        foreach (var child in node.Children) AppendVisible(child, output);
    }

    /// <summary>Thu/mo thu muc: chen hoac go cac dong con ngay sau thu muc (khong dung lai ca danh sach).</summary>
    private void OnNodeExpandedChanged(CompareNode node)
    {
        int index = _flat.IndexOf(node);
        if (index < 0) return;
        if (node.IsExpanded)
        {
            var children = new List<CompareNode>();
            foreach (var child in node.Children) AppendVisible(child, children);
            _flat.InsertRange(index + 1, children);
        }
        else
        {
            int count = 0;
            while (index + 1 + count < _flat.Count && _flat[index + 1 + count].Level > node.Level) count++;
            _flat.RemoveRange(index + 1, count);
        }
    }

    private (long LSum, bool LAny, long RSum, bool RAny, int Work, DateTime? LMax, DateTime? RMax) Aggregate(CompareNode node)
    {
        if (!node.IsFolder)
        {
            var r = node.Row;
            return (r?.LBytes ?? 0, r?.LBytes is not null, r?.RBytes ?? 0, r?.RBytes is not null,
                r is not null && r.Kind != SyncActionKind.Skip && !r.Synced ? 1 : 0, r?.LWhen, r?.RWhen);
        }
        long lSum = 0, rSum = 0; bool lAny = false, rAny = false; int work = 0;
        DateTime? lMax = null, rMax = null;
        foreach (var child in node.Children)
        {
            var a = Aggregate(child);
            lSum += a.LSum; rSum += a.RSum; lAny |= a.LAny; rAny |= a.RAny; work += a.Work;
            if (a.LMax is { } lm && (lMax is null || lm > lMax)) lMax = lm;
            if (a.RMax is { } rm && (rMax is null || rm > rMax)) rMax = rm;
        }
        node.LSizeText = lAny ? FormatSize(lSum) : "(khong co)";
        node.RSizeText = rAny ? FormatSize(rSum) : "(khong co)";
        node.LDateText = FormatWhen(lMax);
        node.RDateText = FormatWhen(rMax);
        node.Direction = work > 0 ? work.ToString() : "";
        int errors = CountErrors(node);
        node.FolderStatus = errors > 0 ? $"{errors} loi" : "";
        return (lSum, lAny, rSum, rAny, work, lMax, rMax);
    }

    private static int CountErrors(CompareNode node)
        => node.IsFolder ? node.Children.Sum(CountErrors) : node.Row?.TrangThai == "LOI" ? 1 : 0;

    // ---------- Tien trinh dong bo - RIENG tung Job ----------

    private static bool IsWork(SyncAction a) => a.Kind is not (SyncActionKind.Skip or SyncActionKind.Conflict);

    /// <summary>
    /// Nhan tien trinh cua 1 Job bat ky (thu cong hoac tu dong, tren UI thread). Luon cap nhat du lieu
    /// cua Job do; chi ve len man hinh neu Job do dang duoc chon.
    /// </summary>
    private void OnSyncProgress(string jobId, SyncProgress p)
    {
        var job = JobOf(jobId);
        if (job is null) return;
        var view = ViewOf(jobId);
        bool shown = IsSelected(jobId);

        switch (p.Stage)
        {
            case SyncProgressStage.Scanning:
            case SyncProgressStage.Hashing:
                if (!view.ProgressVisible || !view.Indeterminate && p.Stage == SyncProgressStage.Scanning && view.ProgressLeft.StartsWith("Hoan tat"))
                    view.Watch.Restart();
                if (!view.Watch.IsRunning) view.Watch.Restart();
                view.ProgressVisible = true;
                view.ErrorCount = 0;
                if (p.Stage == SyncProgressStage.Scanning)
                {
                    view.Indeterminate = true;
                    view.ProgressFraction = 0;
                    view.ProgressLeft = $"Dang quet: {p.BytesDone:N0} file";
                }
                else
                {
                    view.Indeterminate = false;
                    view.ProgressFraction = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
                    view.ProgressLeft = $"Dang so noi dung file cung kich thuoc: {view.ProgressFraction * 100:0}%  •  " +
                                        $"{p.Index:N0} / {p.Total:N0} file  •  {FormatSize(p.BytesDone)} / {FormatSize(p.BytesTotal)}";
                }
                view.ProgressRight = $"Da chay {view.Watch.Elapsed:hh\\:mm\\:ss}";
                view.CurrentFile = p.Message ?? "";
                if (shown) ApplyProgressToUi(view);
                break;

            case SyncProgressStage.Planned:
                view.Rows = BuildRows(job, p.Plan!, p.ScanLeft, p.ScanRight);
                view.SpeedLimitMBps = _runners.TryGetValue(jobId, out var planRunner) ? planRunner.Options.MaxCopySpeedMBps : 0;
                view.Indeterminate = false;
                view.Note = $"Dong bo luc {DateTime.Now:HH:mm:ss}";
                foreach (var (planRow, action) in view.Rows.Zip(p.Plan!))
                    planRow.TrangThai = IsWork(action) ? "Cho" : "";
                view.FilesDone = 0;
                view.FilesTotal = p.Plan!.Count(IsWork);
                view.BytesTotal = p.BytesTotal;
                view.BytesProcessed = view.BytesTransferred = view.CurDone = view.CurTotal = 0;
                view.ErrorCount = 0;
                view.SpeedSamples.Clear();
                view.Watch.Restart();
                view.ProgressVisible = true;
                view.CurrentFile = "";
                UpdateJobProgress(view);
                _jobs.FirstOrDefault(j => j.Job.Id == jobId)?.SetProgress(view.ProgressFraction, view.ProgressLeft, view.ErrorCount > 0);
                if (shown) RenderSelectedView();
                break;

            case SyncProgressStage.Started:
                var running = view.Rows[p.Index];
                running.TrangThai = "Dang chay...";
                view.CurrentFile = $"{KindName(p.Action!.Kind)}: {p.Action.RelativePath}";
                if (shown)
                {
                    if (_nodeByRow.TryGetValue(running, out var node))
                    {
                        node.IsSelected = true;
                        CompareList.ScrollIntoView(node);
                    }
                    CurrentFileText.Text = view.CurrentFile;
                }
                break;

            case SyncProgressStage.Bytes:
                var copying = view.Rows[p.Index];
                view.CurDone = p.BytesDone;
                view.CurTotal = p.BytesTotal;
                double pct = p.BytesTotal > 0 ? p.BytesDone * 100.0 / p.BytesTotal : 100;
                copying.FilePercent = pct;
                copying.ProgressVisibility = Visibility.Visible;
                copying.TrangThai = $"{pct:0}%";
                view.CurrentFile = $"Dang chep: {p.Action!.RelativePath}  ({pct:0}% • {FormatSize(p.BytesDone)} / {FormatSize(p.BytesTotal)})";
                UpdateJobProgress(view);
                _jobs.FirstOrDefault(j => j.Job.Id == jobId)?.SetProgress(view.ProgressFraction, view.ProgressLeft, view.ErrorCount > 0);
                if (shown) ApplyProgressToUi(view);
                break;

            case SyncProgressStage.Verifying:
                view.Rows[p.Index].TrangThai = "Kiem chung...";
                view.CurrentFile = $"Dang kiem chung (hash): {p.Action!.RelativePath}";
                if (shown) CurrentFileText.Text = view.CurrentFile;
                break;

            case SyncProgressStage.Completed:
            case SyncProgressStage.Failed:
                var row = view.Rows[p.Index];
                bool ok = p.Stage == SyncProgressStage.Completed;
                row.ProgressVisibility = Visibility.Collapsed;
                row.TrangThai = ok ? "Xong" : "LOI";
                row.ThoiGian = p.TimeLocal.ToString("dd/MM/yyyy HH:mm:ss");
                if (p.Destination is not null) row.Dich = p.Destination;
                else if (ok && p.Action!.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight)
                    row.Dich = "(da xoa)";
                // Ly do luc Phan tich ("Thieu o dich"...) -> doi thanh KET QUA thuc te, giu ly do cu trong ngoac
                row.LyDo = ok ? $"{DoneText(p.Action!, p.Destination)} ({row.LyDo})" : $"LOI: {p.Error}";
                RefreshRowFromDisk(job, row, ok);
                if (!ok)
                {
                    view.ErrorCount++;
                    if (shown) RefreshChips();
                }
                view.FilesDone++;
                view.BytesProcessed += view.CurTotal;
                view.BytesTransferred += view.CurDone;
                view.CurDone = view.CurTotal = 0;
                UpdateJobProgress(view);
                _jobs.FirstOrDefault(j => j.Job.Id == jobId)?.SetProgress(view.ProgressFraction, view.ProgressLeft, view.ErrorCount > 0);
                if (shown) ApplyProgressToUi(view);
                break;
        }
    }

    /// <summary>
    /// % theo dung luong (fallback theo so file khi Job chi co xoa/doi ten), toc do tuc thoi tren
    /// cua so truot 3 giay + toc do trung binh ca Job, thoi gian con lai = byte con lai / toc do.
    /// </summary>
    private static void UpdateJobProgress(JobView v)
    {
        v.ProgressFraction = v.BytesTotal > 0
            ? Math.Min(1.0, (v.BytesProcessed + v.CurDone) / (double)v.BytesTotal)
            : v.FilesTotal > 0 ? v.FilesDone / (double)v.FilesTotal : 1.0;

        double sec = v.Watch.Elapsed.TotalSeconds;
        long moved = v.BytesTransferred + v.CurDone;
        v.SpeedSamples.Enqueue((sec, moved));
        while (v.SpeedSamples.Count > 2 && sec - v.SpeedSamples.Peek().Sec > SpeedWindow.TotalSeconds)
            v.SpeedSamples.Dequeue();
        var first = v.SpeedSamples.Peek();
        double span = sec - first.Sec;
        double instant = span >= 0.5 ? (moved - first.Bytes) / span : 0;
        double average = sec > 0 ? moved / sec : 0;
        double rate = instant > 0 ? instant : average;

        v.ProgressLeft = (v.BytesTotal > 0
            ? $"Dang dong bo {v.ProgressFraction * 100:0}%  •  {v.FilesDone} / {v.FilesTotal} file  •  {FormatSize(v.BytesProcessed + v.CurDone)} / {FormatSize(v.BytesTotal)}"
            : $"Dang dong bo {v.ProgressFraction * 100:0}%  •  {v.FilesDone} / {v.FilesTotal} file")
            + (v.ErrorCount > 0 ? $"  •  ⚠ {v.ErrorCount} loi" : "");

        if (v.BytesTotal > 0 && rate > 0)
        {
            var remain = TimeSpan.FromSeconds(Math.Max(0, v.BytesTotal - v.BytesProcessed - v.CurDone) / rate);
            v.ProgressRight = $"Con lai {remain:hh\\:mm\\:ss}  •  Toc do {FormatSize((long)rate)}/s (TB {FormatSize((long)average)}/s)" + SpeedLimitSuffix(v);
        }
        else
        {
            v.ProgressRight = $"Da chay {v.Watch.Elapsed:hh\\:mm\\:ss}";
        }
    }

    private static void FinishJobProgress(JobView v, SyncResult r)
    {
        v.Watch.Stop();
        var elapsed = v.Watch.Elapsed;
        double average = elapsed.TotalSeconds > 0 ? v.BytesTransferred / elapsed.TotalSeconds : 0;
        v.ProgressFraction = 1;
        v.ErrorCount = r.Errors.Count;
        v.ProgressLeft = r.Errors.Count > 0
            ? $"⚠ Hoan tat voi {r.Errors.Count} LOI  •  {v.FilesDone - r.Errors.Count} / {v.FilesTotal} file thanh cong  •  {FormatSize(v.BytesTransferred)} da chep  •  Bam chip 'Loi' de xem"
            : $"Hoan tat 100%  •  {v.FilesDone} / {v.FilesTotal} file  •  {FormatSize(v.BytesTransferred)} da chep";
        v.ProgressRight = $"Thoi gian {elapsed:hh\\:mm\\:ss}  •  Toc do TB {FormatSize((long)average)}/s" + SpeedLimitSuffix(v);
        v.CurrentFile = "";
    }

    private static string SpeedLimitSuffix(JobView v) => v.SpeedLimitMBps > 0 ? $"  •  gioi han {v.SpeedLimitMBps} MB/s" : "";

    private void ApplyProgressToUi(JobView v)
    {
        JobProgressPanel.Visibility = v.ProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        JobProgressBar.IsIndeterminate = v.Indeterminate;
        bool bad = v.ErrorCount > 0;
        // Co loi: vien + nen do nhat, thanh tien trinh do, chu do - nhin la thay ngay
        JobProgressPanel.BorderBrush = bad ? ErrorBrush : (Brush)FindResource("CardBorderBrush");
        JobProgressPanel.BorderThickness = new Thickness(bad ? 2 : 1);
        JobProgressPanel.Background = bad ? ErrorBgBrush : Brushes.White;
        JobProgressBar.Foreground = bad ? ErrorBrush : (Brush)FindResource("GreenBrightBrush");
        JobProgressLeft.Foreground = bad ? ErrorBrush : Brushes.Black;
        JobProgressBar.Value = v.ProgressFraction * JobProgressBar.Maximum;
        JobProgressLeft.Text = v.ProgressLeft;
        JobProgressRight.Text = v.ProgressRight;
        CurrentFileText.Text = v.CurrentFile;
    }

    private void Log(string message)
    {
        // Dong loi (LOI / HUY / khong du cho) to do dam, dong co loi le to cam - de nhin thay trong nhat ky
        // Nguoi dung tu bam Huy ("... theo yeu cau") khong phai loi -> khong to do
        bool userCancel = message.Contains("theo yeu cau") || message.StartsWith("Dang huy");
        bool error = !userCancel && (message.Contains("LOI") || message.Contains("HUY") || message.Contains("Khong du cho")
                     || message.Contains("bi loi"));
        bool warn = !error && (userCancel || System.Text.RegularExpressions.Regex.IsMatch(message, @"loi [1-9]"));
        var item = new ListBoxItem { Content = $"[{DateTime.Now:HH:mm:ss}] {message}" };
        if (error) { item.Foreground = ErrorBrush; item.FontWeight = FontWeights.SemiBold; }
        else if (warn) item.Foreground = WarnBrush;
        LogList.Items.Insert(0, item);
    }

    // ---------- Su kien tu JobRunner (da ve UI thread) ----------

    /// <summary>
    /// Runner da bi thay (Job vua sua cau hinh) van con ban su kien - vd luc Dispose bao "Idle".
    /// Su kien do den SAU khi danh sach da ve lai -> neu dung no se ghi de dong Job bang cau hinh CU
    /// (tung lam Job da tat lien tuc van hien nhan LIEN TUC).
    /// </summary>
    private bool IsCurrent(JobRunner r) => _runners.TryGetValue(r.Id, out var cur) && ReferenceEquals(cur, r);

    private void OnRunnerStateChanged(JobRunner r)
    {
        if (!IsCurrent(r)) return;
        _jobs.FirstOrDefault(j => j.Job.Id == r.Id)?.Update(r);
        if (IsSelected(r.Id)) UpdateButtons();
        UpdateTrayStatus();
    }

    // Khong ghi log luc BAT DAU lan chay tu dong: che do lien tuc chay rat thuong xuyen, phan lon
    // khong co gi thay doi -> chi ghi log khi lan chay thuc su chep/xoa/loi (OnRunFinished).
    private void OnRunStarted(JobRunner r, string trigger)
    {
        if (IsCurrent(r)) UpdateButtons();
    }

    private void OnRunFinished(JobRunner runner, JobRunReport report)
    {
        // Lan chay cua runner cu (bi thay giua chung) van hien ket qua; dong Job theo runner hien tai
        if (!IsCurrent(runner) && _runners.TryGetValue(runner.Id, out var current))
        {
            current.InheritHistory(runner);
            _jobs.FirstOrDefault(j => j.Job.Id == runner.Id)?.Update(current);
        }
        else
        {
            _jobs.FirstOrDefault(j => j.Job.Id == runner.Id)?.Update(runner);
        }
        var view = ViewOf(runner.Id);
        bool shown = IsSelected(runner.Id);
        var prefix = report.Trigger == "thu cong" ? "" : $"[Tu dong - {report.Trigger}] ";

        if (report.Error is OperationCanceledException)
        {
            view.ProgressVisible = false;
            view.Indeterminate = false;
            if (shown) ApplyProgressToUi(view);
            Log($"{prefix}Da HUY dong bo Job '{runner.Job.Name}' (chua dong vao file nao).");
            UpdateTrayStatus();
            return;
        }
        if (report.Error is not null)
        {
            Log($"{prefix}LOI khi dong bo Job '{runner.Job.Name}': {report.Error.Message}");
            NotifyIfHidden($"Job '{runner.Job.Name}' bi loi", report.Error.Message);
            UpdateTrayStatus();
            return;
        }
        var outcome = report.Outcome!;
        var r = outcome.Result!;
        foreach (var err in outcome.ScanErrors) Log("Quet: " + err);
        if (r.Aborted)
        {
            view.ProgressVisible = false;
            SetRows(runner.Id, outcome.Actions, $"Da HUY boi chot an toan luc {DateTime.Now:HH:mm:ss} - chua dong vao file nao");
            Log($"{prefix}Job '{runner.Job.Name}' DA HUY BOI CHOT AN TOAN: {r.AbortReason}");
            NotifyIfHidden($"Job '{runner.Job.Name}' da bi HUY", r.AbortReason ?? "Chot an toan");
            UpdateTrayStatus();
            return;
        }

        if (r.Canceled)
        {
            FinishJobProgress(view, r);
            view.ProgressLeft = $"Da HUY theo yeu cau  •  da xong {view.FilesDone} / {view.FilesTotal} file  •  {FormatSize(view.BytesTransferred)} da chep";
            Log($"{prefix}Da HUY dong bo Job '{runner.Job.Name}' giua chung: da xong copy {r.Copied}, xoa {r.Deleted}. File dang chep do da bi bo (khong hong file dich).");
            if (shown) RenderSelectedView();
            UpdateTrayStatus();
            return;
        }
        bool changed = r.Copied + r.Deleted + r.Moved > 0 || r.Errors.Count > 0;
        if (changed || report.Trigger == "thu cong")
        {
            FinishJobProgress(view, r);
            Log($"{prefix}Dong bo xong Job '{runner.Job.Name}': copy {r.Copied}, xoa {r.Deleted}, doi ten {r.Moved}, xung dot {r.Conflicts}, bo qua {r.Skipped}, loi {r.Errors.Count}.");
            foreach (var err in r.Errors) Log("LOI: " + err);
            if (r.Errors.Count > 0)
                NotifyIfHidden($"Job '{runner.Job.Name}': {r.Errors.Count} file loi",
                    r.Errors[0] + (r.Errors.Count > 1 ? $" (va {r.Errors.Count - 1} loi khac)" : ""));
        }
        else
        {
            // Lan chay tu dong khong co gi thay doi: khong ghi log/khong hien thanh tien trinh cho do roi
            view.ProgressVisible = false;
            view.Note = $"Da dong bo, khong co thay doi (kiem tra luc {DateTime.Now:HH:mm:ss})";
        }
        UpdateTrayStatus();
        if (shown)
        {
            RenderSelectedView();
            UpdateDriveHeader(runner.Job);
        }
    }

    // ---------- Thao tac thu cong ----------

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeSelectedAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var runner = SelectedRunner;
        if (runner is not { IsBusy: true }) return;
        runner.Cancel();
        CancelButton.IsEnabled = false; // bat lai khi Job ranh (UpdateButtons)
        Log($"Dang huy Job '{runner.Job.Name}'... (dung o file tiep theo)");
    }

    private async Task AnalyzeSelectedAsync()
    {
        var runner = SelectedRunner;
        if (runner is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        if (runner.IsBusy) { Log($"Job '{runner.Job.Name}' dang chay - thu lai sau."); return; }
        try
        {
            var diag = Diag.Start();
            var view = ViewOf(runner.Id);
            view.ProgressVisible = false;
            view.Watch.Reset();
            var outcome = await runner.AnalyzeAsync(runner.AutoProgress);
            diag.Mark($"quet + so sanh (nen) - hash {outcome?.HashedFiles ?? 0} file / {FormatSize(outcome?.HashedBytes ?? 0)}");
            if (outcome is null)
            {
                // Dang ban (runner da ghi log) hoac nguoi dung bam Huy
                view.ProgressVisible = false;
                view.Indeterminate = false;
                if (IsSelected(runner.Id)) ApplyProgressToUi(view);
                return;
            }
            foreach (var err in outcome.ScanErrors) Log("Quet: " + err);
            int real = outcome.Actions.Count(a => a.Kind != SyncActionKind.Skip);
            ViewOf(runner.Id).ProgressVisible = false;
            view.Indeterminate = false;
            SetRows(runner.Id, outcome.Actions, $"Phan tich luc {DateTime.Now:HH:mm:ss} - chua dong vao file nao",
                outcome.Left, outcome.Right);
            diag.Mark($"BuildRows + RebuildTree (UI thread, {outcome.Actions.Count} dong)");
            Log($"Phan tich xong Job '{runner.Job.Name}': {real} hanh dong can lam / {outcome.Actions.Count} file" +
                (outcome.HashedFiles > 0 ? $" (da so noi dung {outcome.HashedFiles} file, {FormatSize(outcome.HashedBytes)})." : "."));
            if (IsSelected(runner.Id)) UpdateDriveHeader(runner.Job);
            // Do ca thoi gian WPF do layout + ve cay (chay sau khi UI thread ranh)
            _ = Dispatcher.BeginInvoke(() => diag.Mark("layout + render cay (UI thread)"),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            Log($"LOI khi phan tich Job '{runner.Job.Name}': {ex.Message}");
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await SyncSelectedAsync();

    private async Task SyncSelectedAsync()
    {
        var runner = SelectedRunner;
        if (runner is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        if (runner.IsBusy) { Log($"Job '{runner.Job.Name}' dang chay - thu lai sau."); return; }
        // Bang phan tich gan nhat cho biet can chep bao nhieu -> canh bao truoc neu o khong du cho
        var (toLeft, toRight) = RequiredBytes();
        var shortages = new List<string>();
        if (_rightDisk is { Total: > 0 } rd && toRight > rd.Free)
            shortages.Add($"- Folder Phai ({rd.Label}): can {FormatSize(toRight)}, chi con {FormatSize(rd.Free)}");
        if (_leftDisk is { Total: > 0 } ld && toLeft > ld.Free)
            shortages.Add($"- Folder Trai ({ld.Label}): can {FormatSize(toLeft)}, chi con {FormatSize(ld.Free)}");
        if (shortages.Count > 0 && MessageBox.Show(
                "O dia KHONG DU CHO cho lan dong bo nay:\n" + string.Join("\n", shortages) +
                "\n\nCac file khong du cho se bao LOI (khong ghi do dang, khong de lai file tam).\n" +
                "Nen giai phong o dia truoc. Van tiep tuc dong bo?",
                "Canh bao: khong du cho", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (MessageBox.Show($"Dong bo Job '{runner.Job.Name}' ngay bay gio?\nFile bi xoa/ghi de se duoc dua vao .foldersync_history de khoi phuc.",
                "Xac nhan dong bo", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        // Ket thuc va hien thi do OnRunFinished xu ly (chung cho thu cong va tu dong).
        // null = Job vua bat dau chay tu dong trong luc hop thoai dang mo - khong chay chong.
        if (await runner.RunAsync("thu cong", runner.AutoProgress) is null)
            Log($"Job '{runner.Job.Name}' dang chay - bo qua yeu cau dong bo.");
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var runner = SelectedRunner;
        if (runner is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        new HistoryWindow(runner.Job, runner.Options) { Owner = this }.ShowDialog();
    }

    private void AddJob_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new JobOptionsWindow(null) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } job)
        {
            _store.UpsertJob(job.Id, job.Name, job.LeftRoot, job.RightRoot, job.Mode, job.Policy, job.SettingsJson);
            ReloadJobs(selectId: job.Id);
            Log($"Da them Job '{job.Name}'.");
        }
    }

    private void LeftCard_Click(object sender, MouseButtonEventArgs e) => ChangeJobRoot(left: true);
    private void RightCard_Click(object sender, MouseButtonEventArgs e) => ChangeJobRoot(left: false);

    /// <summary>
    /// Bam vao the folder: chon folder moi cho Job dang chon. Doi folder lam state cu mat gia tri
    /// (UpsertJob tu xoa) - hoi xac nhan truoc vi lan chay toi se so sanh lai tu dau.
    /// </summary>
    private void ChangeJobRoot(bool left)
    {
        var runner = SelectedRunner;
        if (runner is null) { MessageBox.Show("Hay chon hoac them mot Job truoc.", "FolderSync"); return; }
        if (runner.IsBusy) { MessageBox.Show("Job dang chay - doi chay xong roi doi folder.", "FolderSync"); return; }
        var job = runner.Job;

        var side = left ? "Trai (nguon)" : "Phai (dich)";
        var current = left ? job.LeftRoot : job.RightRoot;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Chon folder {side} cho Job '{job.Name}'" };
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) != true) return;

        var chosen = dlg.FolderName;
        var other = left ? job.RightRoot : job.LeftRoot;
        if (string.Equals(Path.GetFullPath(chosen).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(Path.GetFullPath(chosen).TrimEnd('\\'), Path.GetFullPath(other).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Folder Trai va Phai khong duoc trung nhau.", "FolderSync");
            return;
        }

        if (MessageBox.Show(
                $"Doi folder {side} cua Job '{job.Name}':\n\n{current}\n->  {chosen}\n\n" +
                "Trang thai dong bo cu se duoc xoa: lan chay toi so sanh lai tu dau giua 2 folder.\n" +
                "Nen bam Phan tich de xem truoc truoc khi Dong bo.",
                "Doi folder", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

        var newLeft = left ? chosen : job.LeftRoot;
        var newRight = left ? job.RightRoot : chosen;
        bool reset = _store.UpsertJob(job.Id, job.Name, newLeft, newRight, job.Mode, job.Policy, job.SettingsJson);
        ReloadJobs(selectId: job.Id); // runner moi theo doi folder moi
        Log($"Da doi folder {side} cua Job '{job.Name}' thanh: {chosen}" +
            (reset ? " (da xoa trang thai dong bo cu)." : "."));
    }

    // ---------- Doi nhanh che do dong bo (bam vung giua 2 the folder) ----------

    private void ModeBox_Click(object sender, MouseButtonEventArgs e)
    {
        var runner = SelectedRunner;
        if (runner is null) { MessageBox.Show("Hay chon hoac them mot Job truoc.", "FolderSync"); return; }
        var menu = new ContextMenu { PlacementTarget = ModeBox, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var mode in new[] { SyncMode.BackupOneWay, SyncMode.MirrorOneWay, SyncMode.TwoWay })
        {
            var item = new MenuItem { Header = ModeName(mode), IsChecked = runner.Job.Mode == mode, Tag = mode };
            item.Click += (_, _) => ChangeJobMode((SyncMode)item.Tag);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// Doi che do cua Job dang chon. Moi che do co tac dong khac nhau len file DANG CO (Mirror xoa file
    /// chi co o dich, 2 chieu chep nguoc ve nguon) va Job lien tuc se chay NGAY -> xac nhan ro tung hau qua.
    /// </summary>
    private void ChangeJobMode(SyncMode mode)
    {
        var runner = SelectedRunner;
        if (runner is null || runner.Job.Mode == mode) return;
        if (runner.IsBusy) { MessageBox.Show("Job dang chay - doi chay xong roi doi che do.", "FolderSync"); return; }
        var job = runner.Job;

        string effect = mode switch
        {
            SyncMode.MirrorOneWay =>
                "Mirror: folder Phai se GIONG HET folder Trai.\n" +
                "- File chi co o Phai se bi XOA (dua vao .foldersync_history, khoi phuc duoc).\n" +
                "- File o Phai bi sua se bi ghi de bang ban o Trai.",
            SyncMode.TwoWay =>
                "Dong bo 2 chieu: thay doi o ben nao cung duoc chep sang ben kia.\n" +
                "- File chi co o Phai se duoc chep NGUOC ve Trai.\n" +
                "- Xoa file o ben nao thi ben kia cung bi xoa (vao history).",
            _ =>
                "Backup 1 chieu: chi chep Trai -> Phai, KHONG xoa gi o Phai."
        };
        string when = runner.Options.AutoOnFileChange
            ? "\n\nJob dang bat DONG BO LIEN TUC: se chay NGAY theo che do moi."
            : "\n\nNen bam Phan tich de xem truoc truoc khi Dong bo.";
        var icon = mode == SyncMode.BackupOneWay ? MessageBoxImage.Question : MessageBoxImage.Warning;
        if (MessageBox.Show($"Doi Job '{job.Name}' tu \"{ModeName(job.Mode)}\"\nsang \"{ModeName(mode)}\"?\n\n{effect}{when}",
                "Doi che do dong bo", MessageBoxButton.OKCancel, icon) != MessageBoxResult.OK) return;

        _store.UpsertJob(job.Id, job.Name, job.LeftRoot, job.RightRoot, mode, job.Policy, job.SettingsJson);
        ReloadJobs(selectId: job.Id);
        Log($"Da doi che do Job '{job.Name}': {ModeName(job.Mode)} -> {ModeName(mode)}.");
    }

    private void EditJob_Click(object sender, RoutedEventArgs e) => EditSelectedJob();

    private void EditSelectedJob()
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        var dlg = new JobOptionsWindow(job) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } edited)
        {
            bool reset = _store.UpsertJob(edited.Id, edited.Name, edited.LeftRoot, edited.RightRoot, edited.Mode, edited.Policy, edited.SettingsJson);
            ReloadJobs(selectId: edited.Id);
            Log($"Da sua Job '{edited.Name}'." +
                (reset ? " Folder da doi -> da xoa trang thai dong bo cu, nen Phan tich lai truoc khi Dong bo." : ""));
        }
    }

    private void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        if (SelectedRunner?.IsBusy == true) { MessageBox.Show("Job dang chay - doi chay xong roi xoa.", "FolderSync"); return; }
        if (MessageBox.Show($"Xoa Job '{job.Name}'? (Chi xoa cau hinh va state, khong dong vao file du lieu.)",
                "Xoa Job", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.DeleteJob(job.Id);
        ReloadJobs();
        Log($"Da xoa Job '{job.Name}'.");
    }

    // ---------- Menu chuot phai / nhap dup tren danh sach Job ----------

    private void JobList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedJob is not null) EditSelectedJob();
    }

    private void JobMenu_Opened(object sender, RoutedEventArgs e)
    {
        var runner = SelectedRunner;
        bool has = runner is not null;
        foreach (var item in JobMenu.Items.OfType<MenuItem>()) item.IsEnabled = has;
        if (runner is null) return;
        MenuAnalyze.IsEnabled = MenuSync.IsEnabled = !runner.IsBusy;
        MenuContinuous.IsChecked = runner.Options.AutoOnFileChange;
    }

    private async void MenuAnalyze_Click(object sender, RoutedEventArgs e) => await AnalyzeSelectedAsync();
    private async void MenuSync_Click(object sender, RoutedEventArgs e) => await SyncSelectedAsync();
    private void MenuOptions_Click(object sender, RoutedEventArgs e) => EditSelectedJob();

    /// <summary>Bat/tat nhanh "dong bo lien tuc" cho Job dang chon (luu vao Job Options).</summary>
    private void MenuContinuous_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) return;
        var o = JobOptions.FromJson(job.SettingsJson);
        o.AutoOnFileChange = !o.AutoOnFileChange;
        _store.UpsertJob(job.Id, job.Name, job.LeftRoot, job.RightRoot, job.Mode, job.Policy, o.ToJson());
        ReloadJobs(selectId: job.Id);
        Log(o.AutoOnFileChange
            ? $"Da BAT dong bo lien tuc cho Job '{job.Name}': co thay doi la tu dong bo sau {o.AutoFileChangeDelaySec} giay."
            : $"Da TAT dong bo lien tuc cho Job '{job.Name}'.");
    }

    internal static string ModeName(SyncMode mode) => mode switch
    {
        SyncMode.BackupOneWay => "Backup 1 chieu (khong xoa o dich)",
        SyncMode.MirrorOneWay => "Mirror 1 chieu (dich giong het nguon)",
        _ => "Dong bo 2 chieu"
    };

    internal static string PolicyName(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.NewerWins => "Ban moi hon thang",
        ConflictPolicy.LeftWins => "Trai thang",
        ConflictPolicy.RightWins => "Phai thang",
        ConflictPolicy.KeepBoth => "Giu ca hai",
        _ => "Hoi thu cong"
    };

    private static string KindName(SyncActionKind kind) => kind switch
    {
        SyncActionKind.CopyLeftToRight => "Copy Trai -> Phai",
        SyncActionKind.CopyRightToLeft => "Copy Phai -> Trai",
        SyncActionKind.DeleteLeft => "Xoa o Trai",
        SyncActionKind.DeleteRight => "Xoa o Phai",
        SyncActionKind.MoveLeft => "Doi ten o Trai",
        SyncActionKind.MoveRight => "Doi ten o Phai",
        SyncActionKind.Conflict => "XUNG DOT",
        _ => "Bo qua"
    };
}

/// <summary>1 dong trong bang hanh dong - cap nhat truc tiep trong luc dong bo.</summary>
public sealed class ActionRow : INotifyPropertyChanged
{
    private string _trangThai = "";
    private string _dich;
    private string _thoiGian = "";
    private string _lyDo;

    public ActionRow(string loai, string duongDan, string dich, string lyDo)
    {
        Loai = loai;
        DuongDan = duongDan;
        _dich = dich;
        _lyDo = lyDo;
    }

    public string Loai { get; }
    public string DuongDan { get; }
    public SyncActionKind Kind { get; init; }
    public string TrangThai { get => _trangThai; set => Set(ref _trangThai, value); }
    public string Dich { get => _dich; set => Set(ref _dich, value); }
    public string ThoiGian { get => _thoiGian; set => Set(ref _thoiGian, value); }
    public string LyDo { get => _lyDo; set => Set(ref _lyDo, value); }

    private double _filePercent;
    private Visibility _progressVisibility = Visibility.Collapsed;
    /// <summary>% da chep cua file nay (chi co y nghia khi ProgressVisibility = Visible).</summary>
    public double FilePercent { get => _filePercent; set => SetValue(ref _filePercent, value); }
    public Visibility ProgressVisibility { get => _progressVisibility; set => SetValue(ref _progressVisibility, value); }

    // Du lieu hien thi kieu GoodSync: size/ngay 2 phia + mui ten chieu
    // Gan luc tao dong; cap nhat lai bang ApplySnapshot sau khi file duoc xu ly xong.
    public string LSizeText { get; set; } = "";
    public string RSizeText { get; set; } = "";
    public string LDateText { get; set; } = "";
    public string RDateText { get; set; } = "";
    public string DirectionText { get; set; } = "";
    public long? LBytes { get; set; }
    public long? RBytes { get; set; }
    public DateTime? LWhen { get; set; }
    public DateTime? RWhen { get; set; }
    /// <summary>Da xu ly xong thanh cong - 2 phia da giong nhau (hien "=" nhu GoodSync).</summary>
    public bool Synced { get; private set; }

    /// <summary>Ten su kien PropertyChanged bao "toan bo so lieu L/R vua doi".</summary>
    public const string SnapshotChanged = "Snapshot";

    public void ApplySnapshot((string Text, long? Bytes, DateTime? When) left,
        (string Text, long? Bytes, DateTime? When) right, bool synced, Func<DateTime?, string> formatWhen)
    {
        (LSizeText, LBytes, LWhen) = left;
        (RSizeText, RBytes, RWhen) = right;
        LDateText = formatWhen(LWhen);
        RDateText = formatWhen(RWhen);
        Synced = synced;
        if (synced) DirectionText = "=";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(SnapshotChanged));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        => SetValue(ref field, value, name);

    private void SetValue<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>1 nut trong cay so sanh kieu GoodSync (folder tong hop tu cac file con).</summary>
public sealed class CompareNode : INotifyPropertyChanged
{
    private static readonly Brush GrayBrush = Frozen(0x94, 0xA3, 0xB8);
    private bool _isSelected;

    public CompareNode() { }

    public CompareNode(ActionRow row)
    {
        Row = row;
        LSizeText = row.LSizeText;
        RSizeText = row.RSizeText;
        LDateText = row.LDateText;
        RDateText = row.RDateText;
        Direction = row.DirectionText;
        LyDo = row.LyDo;
        DirectionBrush = row.Synced ? GrayBrush : row.Kind switch
        {
            SyncActionKind.CopyLeftToRight => Frozen(0x16, 0xA3, 0x4A),
            SyncActionKind.CopyRightToLeft => Frozen(0x25, 0x63, 0xEB),
            SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight => Frozen(0xDC, 0x26, 0x26),
            SyncActionKind.MoveLeft or SyncActionKind.MoveRight => Frozen(0x7C, 0x3A, 0xED),
            SyncActionKind.Conflict => Frozen(0xD9, 0x77, 0x06),
            _ => GrayBrush
        };
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionRow.TrangThai))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // trang thai + mau dong
            else if (e.PropertyName is nameof(ActionRow.FilePercent) or nameof(ActionRow.ProgressVisibility))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
            else if (e.PropertyName == ActionRow.SnapshotChanged)
            {
                LSizeText = row.LSizeText;
                RSizeText = row.RSizeText;
                LDateText = row.LDateText;
                RDateText = row.RDateText;
                Direction = row.DirectionText;
                LyDo = row.LyDo;
                if (row.Synced) DirectionBrush = GrayBrush;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // lam moi moi binding
            }
        };
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public ActionRow? Row { get; }
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public ObservableCollection<CompareNode> Children { get; } = new();
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            ExpandedChanged?.Invoke(this);
        }
    }
    private bool _isExpanded = true;
    /// <summary>MainWindow gan: chen/go dong con trong danh sach phang khi thu/mo thu muc.</summary>
    public Action<CompareNode>? ExpandedChanged { get; set; }

    /// <summary>Cap trong cay (0 = goc). Chi cot ten thut le theo cap - cac cot so lieu luon thang hang.</summary>
    public int Level { get; set; }
    public Thickness Indent => new(Level * 16, 0, 0, 0);
    public Visibility ToggleVisibility => IsFolder ? Visibility.Visible : Visibility.Hidden;
    // Segoe MDL2 Assets: E8B7 = folder, E8A5 = tai lieu
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE8A5";
    public Brush IconBrush => IsFolder ? FolderBrush : GrayBrush;
    private static readonly Brush FolderBrush = Frozen(0xD9, 0x77, 0x06);
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    public FontWeight NameWeight => IsFolder ? FontWeights.Bold : FontWeights.Normal;
    public string LSizeText { get; set; } = "";
    public string RSizeText { get; set; } = "";
    public string LDateText { get; set; } = "";
    public string RDateText { get; set; } = "";
    public string Direction { get; set; } = "";
    public string LyDo { get; set; } = "";
    public Brush DirectionBrush { get; set; } = GrayBrush;
    public string TrangThai => Row?.TrangThai ?? FolderStatus;
    /// <summary>Thu muc: "n loi" neu co file loi ben trong (tinh trong Aggregate).</summary>
    public string FolderStatus { get; set; } = "";

    // Dong loi: nen do nhat + chu do dam o cot Trang thai / Ly do
    private static readonly Brush ErrorRowBrush = Frozen(0xFE, 0xE2, 0xE2);
    private static readonly Brush ErrorTextBrush = Frozen(0xB9, 0x1C, 0x1C);
    private static readonly Brush MutedBrush = Frozen(0x64, 0x74, 0x8B);
    private static readonly Brush OkTextBrush = Frozen(0x15, 0x80, 0x3D);
    private bool HasError => Row is not null ? Row.TrangThai == "LOI" : FolderStatus.Length > 0;
    public Brush RowBackground => HasError ? ErrorRowBrush : Brushes.Transparent;
    public Brush StatusBrush => HasError ? ErrorTextBrush : TrangThai == "Xong" ? OkTextBrush : Brushes.Black;
    public FontWeight StatusWeight => HasError ? FontWeights.Bold : FontWeights.Normal;
    public Brush LyDoBrush => HasError ? ErrorTextBrush : MutedBrush;
    public double FilePercent => Row?.FilePercent ?? 0;
    public Visibility ProgressVisibility => Row?.ProgressVisibility ?? Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// ObservableCollection them/xoa HANG LOAT voi 1 thong bao Reset - chen 40.000 dong tung cai se ban 40.000
/// su kien (va dich mang O(n^2)); ListBox ao hoa chi can 1 lan ve lai.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        RaiseReset();
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        var list = (List<T>)Items;
        list.InsertRange(index, items);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        ((List<T>)Items).RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
