using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderSync.App;

/// <summary>
/// Icon thuong hieu FolderSync (render tu Assets/foldersync.svg) nhung dang PNG base64 trong code,
/// de cua so luon co icon ke ca khi chua sinh file foldersync.ico cho ban build.
/// </summary>
public static class IconHelper
{
    private const string PngBase64 =
        "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" ;

    private static ImageSource? _cached;

    public static ImageSource AppIcon
    {
        get
        {
            if (_cached is not null) return _cached;
            var bytes = Convert.FromBase64String(PngBase64);
            var image = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
            }
            image.Freeze();
            _cached = image;
            return image;
        }
    }
}
