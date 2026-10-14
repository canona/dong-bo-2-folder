using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.App;

public partial class MainWindow : Window
{
    private readonly SqliteStateStore _store;
    private readonly ObservableCollection<JobRecord> _jobs = new();

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
        public string TrangThai { get => _trangThai; set => Set(ref _trangThai, value); }
        public string Dich { get => _dich; set => Set(ref _dich, value); }
        public string ThoiGian { get => _thoiGian; set => Set(ref _thoiGian, value); }
        public string LyDo { get => _lyDo; set => Set(ref _lyDo, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private List<ActionRow> _rows = new();
    private int _progressDone, _progressTotal;

    public MainWindow()
    {
        InitializeComponent();
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

    private void Window_Loaded(object sender, RoutedEventArgs e) => ReloadJobs();

    private void ReloadJobs()
    {
        _jobs.Clear();
        foreach (var job in _store.ListJobs()) _jobs.Add(job);
    }

    private JobRecord? SelectedJob => JobList.SelectedItem as JobRecord;

    private void JobList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var job = SelectedJob;
        JobInfoText.Text = job is null
            ? "Chon hoac them mot Job de bat dau."
            : $"{job.Name}\nTrai: {job.LeftRoot}\nPhai: {job.RightRoot}\nChe do: {ModeName(job.Mode)} | Xung dot: {PolicyName(job.Policy)}";
        ActionGrid.ItemsSource = null;
    }

    private SyncSession? CreateSession()
    {
        var job = SelectedJob;
        if (job is null)
        {
            MessageBox.Show("Hay chon mot Job truoc.", "FolderSync");
            return null;
        }
        return new SyncSession(job.Id, job.LeftRoot, job.RightRoot, job.Mode, _store, policy: job.Policy);
    }

    private void SetBusy(bool busy, string status)
    {
        AnalyzeButton.IsEnabled = !busy;
        SyncButton.IsEnabled = !busy;
        HistoryButton.IsEnabled = !busy;
        StatusText.Text = status;
    }

    private void ShowActions(IEnumerable<SyncAction> actions)
    {
        _rows = actions
            .Select(a => new ActionRow(KindName(a.Kind), a.RelativePath, a.TargetPath ?? "", a.Reason))
            .ToList();
        ActionGrid.ItemsSource = _rows;
    }

    private static bool IsWork(SyncAction a) => a.Kind is not (SyncActionKind.Skip or SyncActionKind.Conflict);

    // Chay tren UI thread (Progress<T> tu chuyen ve SynchronizationContext cua cua so)
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
                ActionGrid.ScrollIntoView(running);
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

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var session = CreateSession();
        if (session is null) return;
        SetBusy(true, "Dang phan tich...");
        try
        {
            var outcome = await Task.Run(session.AnalyzeOnly);
            ShowActions(outcome.Actions);
            foreach (var err in outcome.ScanErrors) Log("Quet: " + err);
            int real = outcome.Actions.Count(a => a.Kind != SyncActionKind.Skip);
            Log($"Phan tich xong: {real} hanh dong can lam / {outcome.Actions.Count} file.");
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
        var session = CreateSession();
        if (session is null) return;
        if (MessageBox.Show("Thuc thi dong bo theo danh sach hanh dong moi nhat?\nFile bi xoa/ghi de se duoc dua vao .foldersync_history de khoi phuc.",
                "Xac nhan dong bo", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

        SetBusy(true, "Dang dong bo...");
        try
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            var progress = new Progress<SyncProgress>(OnSyncProgress);
            var outcome = await Task.Run(() => session.Run(progress));
            var r = outcome.Result!;
            if (r.Aborted)
            {
                ShowActions(outcome.Actions); // Huy truoc khi chay - hien danh sach du dinh de nguoi dung xem
                Log("DA HUY BOI CHOT AN TOAN: " + r.AbortReason);
                SetBusy(false, "Da huy toan bo vi chot an toan - chua dong vao file nao.");
            }
            else
            {
                Log($"Dong bo xong: copy {r.Copied}, xoa {r.Deleted}, doi ten {r.Moved}, xung dot {r.Conflicts}, bo qua {r.Skipped}, loi {r.Errors.Count}.");
                foreach (var err in r.Errors) Log("LOI: " + err);
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
        var dlg = new JobEditWindow(null) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } job)
        {
            _store.UpsertJob(job.Id, job.Name, job.LeftRoot, job.RightRoot, job.Mode, job.Policy);
            ReloadJobs();
            Log($"Da them Job '{job.Name}'.");
        }
    }

    private void EditJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job is null) { MessageBox.Show("Hay chon mot Job truoc.", "FolderSync"); return; }
        var dlg = new JobEditWindow(job) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } edited)
        {
            _store.UpsertJob(edited.Id, edited.Name, edited.LeftRoot, edited.RightRoot, edited.Mode, edited.Policy);
            ReloadJobs();
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
        Log($"Da xoa Job '{job.Name}'.");
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
