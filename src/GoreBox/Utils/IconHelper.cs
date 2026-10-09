using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoreBox.Utils;

/// <summary>Иконки исполняемых файлов и ресурсов приложения.</summary>
public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return Cache.GetOrAdd(path!, p =>
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(p);
                if (icon is null) return null;
                return FromIcon(icon);
            }
            catch
            {
                return null;
            }
        });
    }

    public static ImageSource FromIcon(Icon icon)
    {
        var bitmap = icon.ToBitmap();
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
            bitmap.Dispose();
        }
    }

    public static ImageSource? AppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;
            using var icon = new Icon(stream, 32, 32);
            return FromIcon(icon);
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);
    }

    public static class Colors
    {
        /// <summary>Стабильный цвет по строке — для аваторов приложений без иконки.</summary>
        public static System.Windows.Media.Color FromString(string s)
        {
            var palette = new[]
            {
                "#5C96FF", "#7C6BF0", "#3FB9A4", "#E08A3C", "#D96A9B", "#4FA8DE", "#9C7BE8", "#5FBF6B", "#E5646B"
            };
            var hash = 17;
            foreach (var c in s) hash = hash * 31 + c;
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette[Math.Abs(hash) % palette.Length]);
        }
    }
}
