using System.Windows;

namespace FolderSync.App;

/// <summary>Hop Gioi thieu: phien ban + ban quyen, doc tu AppInfo (metadata assembly).</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Icon = IconHelper.AppIcon;
        LogoImage.Source = IconHelper.AppIcon;
        VersionText.Text = $"Phien ban {AppInfo.Version}";
        OwnerText.Text = $"Ban quyen thuoc ve {AppInfo.Owner}";
        DeveloperText.Text = $"Phat trien thang {AppInfo.DevelopedOn} boi {AppInfo.Developer}";
        CopyrightText.Text = AppInfo.Copyright;
    }

    // IsCancel chi tu dong cua so mo bang ShowDialog; hop nay mo bang Show (khong modal) nen phai Close tay
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
