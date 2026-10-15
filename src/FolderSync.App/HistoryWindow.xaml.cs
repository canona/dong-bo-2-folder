using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FolderSync.Core.Engine;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;

namespace FolderSync.App;

/// <summary>1 dong lich su + trang thai so voi file hien tai (tinh nen, cap nhat sau).</summary>
public sealed class HistoryRow : INotifyPropertyChanged
{
    private static readonly Brush Pending = Frozen(0x64, 0x74, 0x8B);
    private static readonly Brush Missing = Frozen(0xB4, 0x53, 0x09);
    private static readonly Brush Differs = Frozen(0x1D, 0x4E, 0xD8);
    private static readonly Brush Done = Frozen(0x15, 0x80, 0x3D);

    public HistoryRow(HistoryEntry entry)
    {
        Entry = entry;
        StampText = DateTime.TryParseExact(entry.Stamp.Length >= 15 ? entry.Stamp[..15] : entry.Stamp, "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("dd/MM/yyyy HH:mm:ss") : entry.Stamp;
    }

    public HistoryEntry Entry { get; }
    public string StampText { get; }
    public string RelativePath => Entry.RelativePath;
    public string SizeText => MainWindow.FormatSize(Entry.Size);
    public HistoryStatus? Status { get; private set; }
    public bool CanRestore => Status is not HistoryStatus.SameAsCurrent; // dang kiem tra van cho chon
    public string StatusText => Status switch
    {
        null => "Dang kiem tra...",
        HistoryStatus.OriginalMissing => "Chua khoi phuc - file goc da bi xoa",
        HistoryStatus.CurrentDiffers => "Chua khoi phuc - file hien tai khac ban nay",
        _ => "✓ Da khoi phuc (giong file hien tai)"
    };
    public Brush StatusBrush => Status switch
    {
        null => Pending,
        HistoryStatus.OriginalMissing => Missing,
        HistoryStatus.CurrentDiffers => Differs,
        _ => Done
    };
    public FontWeight StatusWeight => Status == HistoryStatus.SameAsCurrent ? FontWeights.SemiBold : FontWeights.Normal;

    public void SetStatus(HistoryStatus status)
    {
        Status = status;
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

public partial class HistoryWindow : Window
{
    private readonly JobRecord _job;
    private readonly JobOptions _options;
    private List<HistoryRow> _rows = new();
    private int _loadVersion;

    public HistoryWindow(JobRecord job, JobOptions options)
    {
        InitializeComponent();
        Icon = IconHelper.AppIcon;
        _job = job;
        _options = options;
        Title = $"Lich su va khoi phuc file - Job '{job.Name}'";
        RefreshList();
    }

    private bool IsRightSide => (string)((ComboBoxItem)SideBox.SelectedItem).Tag == "Right";
    private string CurrentRoot => IsRightSide ? _job.RightRoot : _job.LeftRoot;

    /// <summary>Liet ke ngay, sau do tinh trang thai (can hash) o nen roi cap nhat tung dong.</summary>
    private async void RefreshList()
    {
        int version = ++_loadVersion;
        var root = CurrentRoot;
        UpdateWarning();
        try
        {
            _rows = HistoryService.List(root).Select(e => new HistoryRow(e)).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Khong doc duoc lich su: " + ex.Message, "FolderSync");
            _rows = new();
        }
        ApplyFilter();

        var rows = _rows;
        var statuses = await Task.Run(() => rows.Select(r =>
        {
            try { return HistoryService.GetStatus(root, r.Entry); }
            catch (Exception) { return HistoryStatus.CurrentDiffers; }
        }).ToList());
        if (version != _loadVersion) return; // nguoi dung da doi phia trong luc dang tinh
        for (int i = 0; i < rows.Count; i++) rows[i].SetStatus(statuses[i]);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var shown = HideRestoredCheck.IsChecked == true ? _rows.Where(r => r.CanRestore).ToList() : _rows;
        HistoryGrid.ItemsSource = shown;
        int restored = _rows.Count(r => r.Status == HistoryStatus.SameAsCurrent);
        CountText.Text = $"{_rows.Count} ban luu  •  Da khoi phuc: {restored}  •  Chua khoi phuc: {_rows.Count - restored}";
        UpdateRestoreButton();
    }

    /// <summary>
    /// Job 1 chieu dang dong bo lien tuc: file khoi phuc vao folder DICH khac nguon se bi Job chep de lai
    /// (Mirror con xoa neu nguon khong co) -> canh bao truoc de nguoi dung khong tuong la khoi phuc that bai.
    /// </summary>
    private void UpdateWarning()
    {
        bool oneWayTarget = IsRightSide && _job.Mode != SyncMode.TwoWay;
        bool auto = _options.AutoOnFileChange || _options.AutoPeriodMinutes > 0;
        WarningBox.Visibility = oneWayTarget ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = !oneWayTarget ? "" :
            (auto ? "⚠ Job nay dang tu dong dong bo. " : "⚠ ") +
            (_job.Mode == SyncMode.MirrorOneWay
                ? "Che do Mirror: file khoi phuc vao folder Phai se bi ghi de lai bang ban o folder Trai (hoac bi xoa neu Trai khong co) o lan dong bo tiep theo. Muon giu ban cu, hay chep no ra ngoai hoac khoi phuc o folder Trai."
                : "Che do Backup: file khoi phuc vao folder Phai se bi ghi de lai bang ban o folder Trai o lan dong bo tiep theo neu 2 ban khac nhau. Muon giu ban cu, hay khoi phuc o folder Trai hoac chep ra ngoai.");
    }

    private void SideBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshList();
    }

    private void HideRestored_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private List<HistoryRow> SelectedRestorable() => HistoryGrid.SelectedItems.OfType<HistoryRow>().Where(r => r.CanRestore).ToList();

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRestoreButton();

    private void UpdateRestoreButton()
    {
        int n = SelectedRestorable().Count;
        RestoreButton.IsEnabled = n > 0;
        RestoreButton.Content = n > 1 ? $"Khoi phuc {n} file da chon" : "Khoi phuc file da chon";
    }

    private void SelectAllRestorable_Click(object sender, RoutedEventArgs e)
    {
        HistoryGrid.SelectedItems.Clear();
        foreach (var row in HistoryGrid.Items.OfType<HistoryRow>().Where(r => r.CanRestore))
            HistoryGrid.SelectedItems.Add(row);
        HistoryGrid.Focus();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRestorable();
        if (selected.Count == 0)
        {
            MessageBox.Show("Hay chon it nhat 1 file chua khoi phuc (giu Ctrl/Shift de chon nhieu).", "FolderSync");
            return;
        }
        int samePath = selected.Count - selected.Select(r => r.RelativePath.ToLowerInvariant()).Distinct().Count();
        var note = samePath > 0
            ? $"\n\nCo {samePath} file duoc chon nhieu phien ban - chi khoi phuc ban MOI NHAT cua moi file."
            : "";
        if (MessageBox.Show($"Khoi phuc {selected.Count} ban luu ve vi tri goc trong:\n{CurrentRoot}\n\n" +
                            "File dang co o do se duoc sao vao history truoc (khong mat)." + note,
                "Khoi phuc file", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

        var root = CurrentRoot;
        RestoreButton.IsEnabled = false;
        var results = await Task.Run(() => HistoryService.RestoreMany(root, selected.Select(r => r.Entry)));

        int ok = results.Count(r => r.Restored);
        var skipped = results.Where(r => !r.Restored && !r.Message.StartsWith("Loi")).ToList();
        var failed = results.Where(r => r.Message.StartsWith("Loi")).ToList();
        var lines = new List<string> { $"Da khoi phuc: {ok} file." };
        if (skipped.Count > 0) lines.Add($"Bo qua: {skipped.Count} ({string.Join("; ", skipped.Select(s => s.Message).Distinct())}).");
        if (failed.Count > 0)
        {
            lines.Add($"LOI: {failed.Count} file:");
            lines.AddRange(failed.Take(10).Select(f => $"  - {f.Entry.RelativePath}: {f.Message}"));
            if (failed.Count > 10) lines.Add($"  ... va {failed.Count - 10} file khac");
        }
        MessageBox.Show(string.Join("\n", lines), "Ket qua khoi phuc", MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        RefreshList(); // ban hien tai vua bi thay da duoc them vao history; trang thai cap nhat lai
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
