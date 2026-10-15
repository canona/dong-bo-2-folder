using System.IO;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace FolderSync.App;

/// <summary>
/// Icon o khay he thong (NotifyIcon cua WinForms - WPF khong co san). Luon hien khi app dang chay:
/// app an xuong khay thi day la loi vao duy nhat. Moi thao tac goi nguoc ve MainWindow qua cac Action.
/// NotifyIcon tao tren UI thread cua WPF -> su kien menu chay tren UI thread, goi thang vao cua so duoc.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _startupItem;

    public Action? OpenFull, OpenMini, SyncAll, TogglePause, ToggleStartup, About, Exit;

    public TrayIconService()
    {
        var menu = new Forms.ContextMenuStrip();
        var full = new Forms.ToolStripMenuItem("Mo FolderSync (day du)", null, (_, _) => OpenFull?.Invoke())
        {
            Font = new Drawing.Font(Forms.Control.DefaultFont, Drawing.FontStyle.Bold)
        };
        menu.Items.Add(full);
        menu.Items.Add("Che do Mini", null, (_, _) => OpenMini?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Dong bo tat ca Job ngay", null, (_, _) => SyncAll?.Invoke());
        _pauseItem = new Forms.ToolStripMenuItem("Tam dung tu dong dong bo", null, (_, _) => TogglePause?.Invoke());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = new Forms.ToolStripMenuItem("Chay cung Windows", null, (_, _) => ToggleStartup?.Invoke());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Gioi thieu FolderSync", null, (_, _) => About?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Thoat FolderSync", null, (_, _) => Exit?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = $"FolderSync - {AppInfo.ShortNotice}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenFull?.Invoke();
        menu.Opening += (_, _) => _startupItem.Checked = StartupManager.IsEnabled();
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            // Icon nhung trong exe (ApplicationIcon) - net nhat o moi do phan giai
            if (Environment.ProcessPath is { } exe && Drawing.Icon.ExtractAssociatedIcon(exe) is { } fromExe)
                return fromExe;
        }
        catch (Exception) { /* roi xuong ban PNG */ }
        using var ms = new MemoryStream(IconHelper.PngBytes);
        using var bmp = new Drawing.Bitmap(new Drawing.Bitmap(ms), new Drawing.Size(32, 32));
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Tooltip khay (Windows gioi han 127 ky tu).</summary>
    public void SetStatus(string text)
    {
        var t = "FolderSync - " + text;
        _icon.Text = t.Length > 127 ? t[..124] + "..." : t;
    }

    public void SetPaused(bool paused)
        => _pauseItem.Text = paused ? "Tiep tuc tu dong dong bo" : "Tam dung tu dong dong bo";

    public void Notify(string title, string message, bool error)
        => _icon.ShowBalloonTip(5000, title, message, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false; // khong de "icon ma" o khay sau khi thoat
        _icon.Dispose();
    }
}
