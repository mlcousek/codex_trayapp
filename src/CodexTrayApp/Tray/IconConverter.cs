using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using CodexTrayApp.Interop;

namespace CodexTrayApp.Tray;

/// <summary>
/// Turns a rendered WPF bitmap into a <see cref="Icon"/> the shell can show. H.NotifyIcon's ImageSource path does
/// not accept RenderTargetBitmap, so the conversion goes through GDI+ with the temporary handle released explicitly.
/// </summary>
internal static class IconConverter
{
    public static Icon ToIcon(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        stream.Position = 0;

        using var bitmap = new Bitmap(stream);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
