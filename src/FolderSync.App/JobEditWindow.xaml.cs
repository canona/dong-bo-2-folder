using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderSync.Core.Models;
using FolderSync.Core.Storage;
using Microsoft.Win32;

namespace FolderSync.App;

public partial class JobEditWindow : Window
{
    private readonly string _id;
    public JobRecord? Result { get; private set; }

    public JobEditWindow(JobRecord? existing)
    {
        InitializeComponent();
        _id = existing?.Id ?? Guid.NewGuid().ToString("N");
        NameBox.Text = existing?.Name ?? "";
        LeftBox.Text = existing?.LeftRoot ?? "";
        RightBox.Text = existing?.RightRoot ?? "";
        SelectByTag(ModeBox, (existing?.Mode ?? SyncMode.TwoWay).ToString());
        SelectByTag(PolicyBox, (existing?.Policy ?? ConflictPolicy.NewerWins).ToString());
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private string? BrowseFolder(string current)
    {
        var dlg = new OpenFolderDialog { Title = "Chon folder" };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dlg.InitialDirectory = current;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void BrowseLeft_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseFolder(LeftBox.Text) is { } path) LeftBox.Text = path;
    }

    private void BrowseRight_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseFolder(RightBox.Text) is { } path) RightBox.Text = path;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) ||
            string.IsNullOrWhiteSpace(LeftBox.Text) ||
            string.IsNullOrWhiteSpace(RightBox.Text))
        {
            MessageBox.Show("Hay dien day du Ten Job, Folder Trai va Folder Phai.", "FolderSync");
            return;
        }
        if (string.Equals(LeftBox.Text.Trim(), RightBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Folder Trai va Folder Phai khong duoc trung nhau.", "FolderSync");
            return;
        }
        Result = new JobRecord(_id, NameBox.Text.Trim(), LeftBox.Text.Trim(), RightBox.Text.Trim(),
            Enum.Parse<SyncMode>(TagOf(ModeBox)), Enum.Parse<ConflictPolicy>(TagOf(PolicyBox)));
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
