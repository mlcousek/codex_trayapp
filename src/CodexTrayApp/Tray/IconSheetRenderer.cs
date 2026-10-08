using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Tray;

/// <summary>
/// Development aid behind <c>--render-icons</c>: writes contact sheets of the tray icon in every state and size,
/// on a dark and a light taskbar, with a 4x nearest-neighbour blow-up next to each native rendering.
/// </summary>
public static class IconSheetRenderer
{
    private const int Zoom = 4;
    private const int Gap = 12;
    private const int Margin = 16;

    private static readonly TrayIconState[] States =
    [
        TrayIconState.Unknown,
        new(12, UsageWindowStatus.Ok, false),
        new(45, UsageWindowStatus.Ok, false),
        new(72, UsageWindowStatus.Warning, false),
        new(86, UsageWindowStatus.Warning, true),
        new(93, UsageWindowStatus.Critical, false),
        new(100, UsageWindowStatus.Exhausted, false),
    ];

    public static IReadOnlyList<string> RenderSheets(string directory, TrayPalette darkTaskbar, TrayPalette lightTaskbar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        return
        [
            RenderSheet(Path.Combine(directory, "tray-icons-dark.png"), darkTaskbar, Color.FromRgb(0x20, 0x20, 0x20)),
            RenderSheet(Path.Combine(directory, "tray-icons-light.png"), lightTaskbar, Color.FromRgb(0xF3, 0xF3, 0xF3)),
        ];
    }

    private static string RenderSheet(string path, TrayPalette palette, Color taskbar)
    {
        var sizes = TrayIconRenderer.StandardSizes;
        var largest = sizes[^1];
        var columnWidth = largest + Gap + (largest * Zoom) + Gap;
        var rowHeight = (largest * Zoom) + Gap;
        var width = Margin + (sizes.Count * columnWidth) + Margin;
        var height = Margin + 20 + (States.Length * rowHeight) + Margin;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        var background = new SolidColorBrush(taskbar);
        background.Freeze();
        var label = new SolidColorBrush(palette.Text);
        label.Freeze();
        var typeface = new Typeface("Segoe UI");

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));

            for (var column = 0; column < sizes.Count; column++)
            {
                var caption = new FormattedText(
                    string.Create(CultureInfo.InvariantCulture, $"{sizes[column]} px"),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    12,
                    label,
                    1.0);
                context.DrawText(caption, new Point(Margin + (column * columnWidth), Margin));
            }

            for (var row = 0; row < States.Length; row++)
            {
                var y = Margin + 20 + (row * rowHeight);
                for (var column = 0; column < sizes.Count; column++)
                {
                    var size = sizes[column];
                    var x = Margin + (column * columnWidth);
                    var bitmap = TrayIconRenderer.Render(States[row], size, palette);
                    context.DrawImage(bitmap, new Rect(x, y, size, size));
                    context.DrawImage(bitmap, new Rect(x + largest + Gap, y, size * Zoom, size * Zoom));
                }
            }
        }

        var sheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
