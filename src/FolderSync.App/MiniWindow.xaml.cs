using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace FolderSync.App;

/// <summary>
/// Che do Mini: cua so nho o goc man hinh, co the ghim tren cung. Khong tu chay Job - moi thao tac
/// (dong bo, mo day du, an) do MainWindow xu ly de giu 1 noi dieu phoi duy nhat.
/// Bam X: an cua so (app van chay o khay), khong thoat.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly MainWindow _owner;

    public MiniWindow(MainWindow owner, ObservableCollection<JobListItem> jobs, bool topmost)
    {
        InitializeComponent();
        _owner = owner;
        Icon = IconHelper.AppIcon;
        LogoImage.Source = IconHelper.AppIcon;
        JobList.ItemsSource = jobs;
        PinButton.IsChecked = topmost;
        Topmost = topmost;
        PlaceBottomRight();
    }

    /// <summary>Goc duoi phai vung lam viec (tren thanh taskbar), nhu cac app chay nen.</summary>
    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    public void SetSummary(string text) => SummaryText.Text = text;

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && PinButton.IsChecked == Topmost) return; // gia tri khoi tao trong constructor
        Topmost = PinButton.IsChecked == true;
        _owner.SetMiniTopmost(Topmost);
    }

    private async void SyncJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is JobListItem item) await _owner.SyncJobFromShortcutAsync(item.Job.Id);
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e) => await _owner.SyncAllAsync();

    private void Full_Click(object sender, RoutedEventArgs e) => _owner.ShowFull();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_owner.IsExiting) return; // thoat app that su -> cho dong
        e.Cancel = true;
        _owner.HideMini();
    }
}
