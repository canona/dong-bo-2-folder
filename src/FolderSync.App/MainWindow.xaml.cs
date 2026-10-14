using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.App;

public partial class MainWindow : Window
{
    private readonly SqliteStateStore _store;
    private readonly ObservableCollection<JobRecord> _jobs = new();

    private List<ActionRow> _rows = new();
    private readonly Dictionary<ActionRow, CompareNode> _nodeByRow = new();
    private JobRecord? _activeJob;
    private bool _busy;
    private int _progressDone, _progressTotal;

    // Ha tang tu dong chay (tab Tu dong cua Job Options)
    private readonly List<DispatcherTimer> _autoTimers = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _autoGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        Icon = IconHelper.AppIcon;
        AppIconImage.Source = IconHelper.AppIcon;
        _store = new SqliteStateStore(ResolveDbPath());
        JobList.ItemsSource = _jobs;
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

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ReloadJobs();
        SetupAutoJobs(runOnStart: true);
    }

    private void ReloadJobs()
    {
        _jobs.Clear();
        foreach (var job in _store.ListJobs()) _jobs.Add(job);
    }

    private JobRecord? SelectedJob => JobList.SelectedItem as JobRecord;

    private void JobList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _activeJob = SelectedJob;
        _rows = new();
        CompareTree.ItemsSource = null;
        RefreshChips();
        if (_activeJob is null)
        {
            LeftPathText.Text = "(chua chon Job)";
            RightPathText.Text = "(chua chon Job)";
            LeftDriveText.Text = RightDriveText.Text = RequiredText.Text = "";
            ModeArrowText.Text = "⇄";
            ModeNameText.Text = "";
            SummaryText.Text = "";
            return;
        }
        UpdateDriveHeader(_activeJob);
        SummaryText.Text = "Bam Phan tich de xem truoc cac thay doi.";
    }

    private void UpdateDriveHeader(JobRecord job)
    {
        LeftPathText.Text = job.LeftRoot;
        RightPathText.Text = job.RightRoot;
        LeftDriveText.Text = DriveText(job.LeftRoot);
        RightDriveText.Text = DriveText(job.RightRoot);
        ModeArrowText.Text = job.Mode == SyncMode.TwoWay ? "⇄" : "→";
        ModeNameText.Text = job.Mode switch
        {
            SyncMode.TwoWay => "Dong bo 2 chieu",
            SyncMode.MirrorOneWay => "Mirror 1 chieu",
            _ => "Backup 1 chieu"
        };
    }

    private static string DriveText(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "";
            var d = new DriveInfo(root);
            if (!d.IsReady) return $"{root.TrimEnd('\\')}: o chua san sang";
            return $"{root.TrimEnd('\\')} {d.DriveFormat} • Trong: {FormatSize(d.AvailableFreeSpace)} / Tong: {FormatSize(d.TotalSize)}";
        }
        catch { return ""; }
    }

    // ---------- Khoi tao Session tu Job + JobOptions ----------

    private SyncSession CreateSessionFor(JobRecord job, JobOptions o) => new(
        job.Id, job.LeftRoot, job.RightRoot, job.Mode, _store,
        options: new SyncOptions(
            MaxDeletePercent: o.SafetyMaxDeletePercent,
            CopyCreationTime: o.CopyCreationTime,
            CopyAttributes: o.CopyAttributes),
        policy: job.Policy,
        filter: o.BuildFilter(),
        detectRenames: o.DetectRenames,
        computeHashes: o.CompareChecksums,
        backupPropagateDeletes: o.PropagateDeletes,
        timeTolerance: o.TimeTolerance,
        createMissingRoots: o.CreateFoldersIfMissing);

    private void PostRunCleanup(JobRecord job, JobOptions o)
    {
        if (!o.CleanupHistory) return;
        try
        {
            int n = HistoryService.Cleanup(job.LeftRoot, o.HistoryRetentionDays)
                  + HistoryService.Cleanup(job.RightRoot, o.HistoryRetentionDays);
            if (n > 0) Log($"Da don {n} muc history cu (qua {o.HistoryRetentionDays} ngay) cua Job '{job.Name}'.");
        }
        catch (Exception ex) { Log("Don history loi: " + ex.Message); }
    }

    private void RunScript(string command, JobRecord job, string result, SyncResult? r)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        int changed = r is null ? 0 : r.Copied + r.Deleted + r.Moved;
        var expanded = command
            .Replace("%JOBNAME%", job.Name)
            .Replace("%RESULT%", result)
            .Replace("%CHANGED%", changed.ToString())
            .Replace("%ERRORS%", (r?.Errors.Count ?? 0).ToString())
            .Replace("%CONFLICTS%", (r?.Conflicts ?? 0).ToString())
            .Replace("%LF%", job.LeftRoot)
            .Replace("%RF%", job.RightRoot);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/C " + expanded)
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is not null && !p.WaitForExit(30_000))
                Log($"Script chay qua 30 giay, bo qua cho: {command}");
        }
        catch (Exception ex) { Log("Script loi: " + ex.Message); }
    }

    // ---------- Hien thi: dong hanh dong + cay so sanh ----------

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        SyncButton.IsEnabled = !busy;
        HistoryButton.IsEnabled = !busy;
        StatusText.Text = status;
    }

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

    private void ShowActions(IEnumerable<SyncAction> actions)
    {
        var job = _activeJob;
        _rows = actions.Select(a =>
        {
            var (lText, lBytes, lWhen) = job is null ? ("", null, null) : Probe(job.LeftRoot, a.RelativePath);
            var (rText, rBytes, rWhen) = job is null ? ("", null, null) : Probe(job.RightRoot, a.RelativePath);
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

        long required = _rows.Sum(r => r.Kind switch
        {
            SyncActionKind.CopyLeftToRight => r.LBytes ?? 0,
            SyncActionKind.CopyRightToLeft => r.RBytes ?? 0,
            _ => 0L
        });
        RequiredText.Text = required > 0 ? $"Can chep: {FormatSize(required)}" : "";
        RefreshChips();
        RebuildTree();
    }

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
        FilterAll.Content = $"Tat ca ({_rows.Count})";
        FilterChanges.Content = $"Thay doi ({_rows.Count(r => r.Kind != SyncActionKind.Skip)})";
        FilterConflicts.Content = $"Xung dot ({_rows.Count(r => r.Kind == SyncActionKind.Conflict)})";
        FilterDeletes.Content = $"Xoa ({_rows.Count(r => r.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight)})";
        FilterErrors.Content = $"Loi ({_rows.Count(r => r.TrangThai == "LOI")})";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RebuildTree();
    }

    private void RebuildTree()
    {
        var filtered = _rows.Where(PassesFilter).OrderBy(r => r.DuongDan, StringComparer.OrdinalIgnoreCase).ToList();
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
                    folder = new CompareNode { Name = parts[i], IsFolder = true };
                    folders[acc] = folder;
                    if (parent is null) roots.Add(folder); else parent.Children.Add(folder);
                }
                parent = folder;
            }
            var leaf = new CompareNode(row) { Name = parts[^1] };
            if (parent is null) roots.Add(leaf); else parent.Children.Add(leaf);
            _nodeByRow[row] = leaf;
        }

        foreach (var root in roots) Aggregate(root);
        CompareTree.ItemsSource = roots;
        SummaryText.Text = _rows.Count == 0 ? "" : $"Dang hien {filtered.Count} / {_rows.Count} muc";
    }

    private (long LSum, bool LAny, long RSum, bool RAny, int Work, DateTime? LMax, DateTime? RMax) Aggregate(CompareNode node)
    {
        if (!node.IsFolder)
        {
            var r = node.Row;
            return (r?.LBytes ?? 0, r?.LBytes is not null, r?.RBytes ?? 0, r?.RBytes is not null,
                r is not null && r.Kind != SyncActionKind.Skip ? 1 : 0, r?.LWhen, r?.RWhen);
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
        return (lSum, lAny, rSum, rAny, work, lMax, rMax);
    }

    // ---------- Tien trinh dong bo (tu SyncSession/SyncExecutor) ----------

    private static bool IsWork(SyncAction a) => a.Kind is not (SyncActionKind.Skip or SyncActionKind.Conflict);

    private void OnSyncProgress(SyncProgress p)
    {
        switch (p.Stage)
        {
            case SyncProgressStage.Planned:
                ShowActions(p.Plan!);
                foreach (var (planRow, action) in _rows.Zip(p.Plan!))
                    planRow.TrangThai = IsWork(action) ? "Cho" : "";
                _progressDone = 0;
                _progressTotal = p.Plan!.Count(IsWork);
                SyncProgressBar.Maximum = Math.Max(1, _progressTotal);
                SyncProgressBar.Value = 0;
                ProgressText.Text = $"0 / {_progressTotal}";
                ProgressPanel.Visibility = Visibility.Visible;
                break;

            case SyncProgressStage.Started:
                var running = _rows[p.Index];
                running.TrangThai = "Dang chay...";
                if (_nodeByRow.TryGetValue(running, out var node)) node.IsSelected = true;
                StatusText.Text = $"{KindName(p.Action!.Kind)}: {p.Action.RelativePath}";
                break;

            case SyncProgressStage.Completed:
            case SyncProgressStage.Failed:
                var row = _rows[p.Index];
                bool ok = p.Stage == SyncProgressStage.Completed;
                row.TrangThai = ok ? "Xong" : "LOI";
                row.ThoiGian = p.TimeLocal.ToString("dd/MM/yyyy HH:mm:ss");
                if (p.Destination is not null) row.Dich = p.Destination;
                else if (ok && p.Action!.Kind is SyncActionKind.DeleteLeft or SyncActionKind.DeleteRight)
                    row.Dich = "(da xoa)";
                if (!ok) row.LyDo = p.Error ?? row.LyDo;
                _progressDone++;
                SyncProgressBar.Value = _progressDone;
                ProgressText.Text = $"{_progressDone} / {_progressTotal}";
                break;
        }
    }

    private void Log(string message) => LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    // ---------- Thao tac thu cong ----------

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        _activeJob = job;
        var o = JobOptions.FromJson(job.SettingsJson);
        var session = CreateSessionFor(job, o);
        SetBusy(true, "Dang phan tich...");
        try
        {
            var outcome = await Task.Run(() =>
            {
                RunScript(o.ScriptPreAnalyze, job, "", null);
                return session.AnalyzeOnly();
            });
            ShowActions(outcome.Actions);
            foreach (var err in outcome.ScanErrors) Log("Quet: " + err);
            int real = outcome.Actions.Count(a => a.Kind != SyncActionKind.Skip);
            Log($"Phan tich xong Job '{job.Name}': {real} hanh dong can lam / {outcome.Actions.Count} file.");
            UpdateDriveHeader(job);
            SetBusy(false, "Da phan tich - chua dong vao file nao. Bam Dong bo de thuc thi.");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Loi khi phan tich.");
            Log("LOI: " + ex.Message);
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        if (MessageBox.Show("Thuc thi dong bo theo danh sach hanh dong moi nhat?\nFile bi xoa/ghi de se duoc dua vao .foldersync_history de khoi phuc.",
                "Xac nhan dong bo", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

        _activeJob = job;
        var o = JobOptions.FromJson(job.SettingsJson);
        var session = CreateSessionFor(job, o);
        SetBusy(true, "Dang dong bo...");
        try
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            var progress = new Progress<SyncProgress>(OnSyncProgress);
            var outcome = await Task.Run(() =>
            {
                RunScript(o.ScriptPreAnalyze, job, "", null);
                var oc = session.Run(progress);
                RunScript(o.ScriptPostSync, job, oc.Result!.Aborted ? "aborted" : "", oc.Result);
                return oc;
            });
            var r = outcome.Result!;
            foreach (var err in outcome.ScanErrors) Log("Quet: " + err);
            if (r.Aborted)
            {
                ShowActions(outcome.Actions);
                Log("DA HUY BOI CHOT AN TOAN: " + r.AbortReason);
                SetBusy(false, "Da huy toan bo vi chot an toan - chua dong vao file nao.");
            }
            else
            {
                Log($"Dong bo xong Job '{job.Name}': copy {r.Copied}, xoa {r.Deleted}, doi ten {r.Moved}, xung dot {r.Conflicts}, bo qua {r.Skipped}, loi {r.Errors.Count}.");
                foreach (var err in r.Errors) Log("LOI: " + err);
                PostRunCleanup(job, o);
                RefreshChips();
                RebuildTree();
                UpdateDriveHeader(job);
                SetBusy(false, "Dong bo hoan tat.");
            }
        }
        catch (Exception ex)
        {
            SetBusy(false, "Loi khi dong bo.");
            Log("LOI: " + ex.Message);
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        new HistoryWindow(job.LeftRoot, job.RightRoot) { Owner = this }.ShowDialog();
    }

    private void AddJob_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new JobOptionsWindow(null) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } job)
        {
            _store.UpsertJob(job.Id, job.Name, job.LeftRoot, job.RightRoot, job.Mode, job.Policy, job.SettingsJson);
            ReloadJobs();
            SetupAutoJobs();
            Log($"Da them Job '{job.Name}'.");
        }
    }

    private void EditJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        var dlg = new JobOptionsWindow(job) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } edited)
        {
            _store.UpsertJob(edited.Id, edited.Name, edited.LeftRoot, edited.RightRoot, edited.Mode, edited.Policy, edited.SettingsJson);
            ReloadJobs();
            SetupAutoJobs();
            Log($"Da sua Job '{edited.Name}'. Luu y: doi che do/folder thi nen Phan tich lai tu dau.");
        }
    }

    private void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        if (MessageBox.Show($"Xoa Job '{job.Name}'? (Chi xoa cau hinh va state, khong dong vao file du lieu.)",
                "Xoa Job", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.DeleteJob(job.Id);
        ReloadJobs();
        SetupAutoJobs();
        Log($"Da xoa Job '{job.Name}'.");
    }

    // ---------- Tu dong chay (tab Tu dong) ----------

    private void SetupAutoJobs(bool runOnStart = false)
    {
        foreach (var t in _autoTimers) t.Stop();
        _autoTimers.Clear();
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();

        var startJobs = new List<JobRecord>();
        foreach (var job in _jobs)
        {
            var o = JobOptions.FromJson(job.SettingsJson);
            if (runOnStart && o.AutoOnStart) startJobs.Add(job);

            if (o.AutoPeriodMinutes > 0)
            {
                var captured = job;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(o.AutoPeriodMinutes) };
                timer.Tick += async (_, _) => await RunJobAutoAsync(captured, "dinh ky");
                timer.Start();
                _autoTimers.Add(timer);
            }

            if (o.AutoOnFileChange)
            {
                var capturedJob = job;
                var debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, o.AutoFileChangeDelaySec)) };
                debounce.Tick += async (_, _) => { debounce.Stop(); await RunJobAutoAsync(capturedJob, "file thay doi"); };
                _autoTimers.Add(debounce);
                foreach (var root in new[] { job.LeftRoot, job.RightRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root)) continue;
                    try
                    {
                        var watcher = new FileSystemWatcher(root)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                           | NotifyFilters.LastWrite | NotifyFilters.Size,
                            EnableRaisingEvents = true
                        };
                        void OnChange(object _, FileSystemEventArgs ev)
                        {
                            if (ev.FullPath.Contains(".foldersync_history")) return;
                            Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
                        }
                        watcher.Changed += OnChange;
                        watcher.Created += OnChange;
                        watcher.Deleted += OnChange;
                        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
                        _watchers.Add(watcher);
                    }
                    catch (Exception ex) { Log($"Khong theo doi duoc '{root}': {ex.Message}"); }
                }
            }
        }

        if (startJobs.Count > 0)
        {
            _ = Dispatcher.BeginInvoke(async () =>
            {
                foreach (var job in startJobs) await RunJobAutoAsync(job, "khi mo app");
            });
        }
    }

    private async Task RunJobAutoAsync(JobRecord job, string reason)
    {
        if (_busy) return;
        if (!await _autoGate.WaitAsync(0)) return;
        SetBusy(true, $"Tu dong ({reason}): dang dong bo Job '{job.Name}'...");
        try
        {
            var o = JobOptions.FromJson(job.SettingsJson);
            var outcome = await Task.Run(() =>
            {
                RunScript(o.ScriptPreAnalyze, job, "", null);
                var oc = CreateSessionFor(job, o).Run(null);
                RunScript(o.ScriptPostSync, job, oc.Result!.Aborted ? "aborted" : "", oc.Result);
                return oc;
            });
            var r = outcome.Result!;
            foreach (var err in outcome.ScanErrors) Log("Quet: " + err);
            if (r.Aborted)
                Log($"[Tu dong] Job '{job.Name}' bi HUY boi chot an toan: {r.AbortReason}");
            else
                Log($"[Tu dong - {reason}] Job '{job.Name}': copy {r.Copied}, xoa {r.Deleted}, doi ten {r.Moved}, xung dot {r.Conflicts}, loi {r.Errors.Count}.");
            PostRunCleanup(job, o);
            if (SelectedJob?.Id == job.Id)
            {
                _activeJob = job;
                ShowActions(outcome.Actions);
                UpdateDriveHeader(job);
            }
            SetBusy(false, "San sang.");
        }
        catch (Exception ex)
        {
            Log("[Tu dong] LOI: " + ex.Message);
            SetBusy(false, "San sang.");
        }
        finally { _autoGate.Release(); }
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

    // Du lieu hien thi kieu GoodSync: size/ngay 2 phia + mui ten chieu
    public string LSizeText { get; init; } = "";
    public string RSizeText { get; init; } = "";
    public string LDateText { get; init; } = "";
    public string RDateText { get; init; } = "";
    public string DirectionText { get; init; } = "";
    public long? LBytes { get; init; }
    public long? RBytes { get; init; }
    public DateTime? LWhen { get; init; }
    public DateTime? RWhen { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
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
        DirectionBrush = row.Kind switch
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrangThai)));
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
    public bool IsExpanded { get; set; } = true;
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
    public string TrangThai => Row?.TrangThai ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;
}
