using System.Windows;
using System.Windows.Controls;
using FolderSync.Core.Engine;

namespace FolderSync.App;

public partial class HistoryWindow : Window
{
    private readonly string _leftRoot;
    private readonly string _rightRoot;

    public HistoryWindow(string leftRoot, string rightRoot)
    {
        InitializeComponent();
        _leftRoot = leftRoot;
        _rightRoot = rightRoot;
        RefreshList();
    }

    private string CurrentRoot =>
        (string)((ComboBoxItem)SideBox.SelectedItem).Tag == "Left" ? _leftRoot : _rightRoot;

    private void RefreshList()
    {
        try { HistoryGrid.ItemsSource = HistoryService.List(CurrentRoot); }
        catch (Exception ex) { MessageBox.Show("Khong doc duoc lich su: " + ex.Message, "FolderSync"); }
    }

    private void SideBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshList();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryEntry entry)
        {
            MessageBox.Show("Hay chon mot file trong danh sach.", "FolderSync");
            return;
        }
        if (MessageBox.Show($"Khoi phuc '{entry.RelativePath}' (ban luu luc {entry.Stamp}) ve vi tri goc?\nNeu dang co file o do, no se duoc sao luu vao history truoc.",
                "Khoi phuc file", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            var dest = HistoryService.Restore(CurrentRoot, entry);
            MessageBox.Show("Da khoi phuc ve: " + dest, "FolderSync");
            RefreshList();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Khoi phuc that bai: " + ex.Message, "FolderSync");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
