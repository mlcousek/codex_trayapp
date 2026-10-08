using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexTrayApp.Diagnostics;

/// <summary>
/// Development aid behind <c>--capture-flyout</c>: renders the flyout's visual tree at the window's DPI onto the
/// solid surface colour. A screen copy was tried first, but it depends on the desktop being composed at that moment
/// (it came back blank on a locked session), while this render is the same on every machine.
/// </summary>
internal static class FlyoutCapture
{
    public static string Capture(Window window, string path)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // FlyoutWindow's content is a Border directly; SettingsWindow wraps its Border in a ScrollViewer.
        var root = window.Content as Border ?? (window.Content as ScrollViewer)?.Content as Border;
        var previous = root?.Background;
        if (root is not null && window.TryFindResource("SurfaceBrush") is Brush surface)
        {
            root.Background = surface;
            root.UpdateLayout();
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var target = new RenderTargetBitmap(
                (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            target.Render(window);

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (var stream = File.Create(fullPath))
            {
                encoder.Save(stream);
            }

            return fullPath;
        }
        finally
        {
            root?.Background = previous;
        }
    }
}
