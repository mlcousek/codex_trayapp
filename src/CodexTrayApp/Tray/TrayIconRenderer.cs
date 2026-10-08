using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Tray;

/// <summary>
/// Draws the tray icon: a ring-arc of the primary window's utilisation around a compact numeral.
/// Rendered at the exact pixel size the taskbar wants (16 px at 100 % DPI) so nothing is resampled.
/// </summary>
public static class TrayIconRenderer
{
    /// <summary>Pixel sizes the taskbar uses at 100, 125, 150 and 200 % DPI.</summary>
    public static IReadOnlyList<int> StandardSizes { get; } = [16, 20, 24, 32];

    private static readonly Typeface NumeralTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static BitmapSource Render(TrayIconState state, int size, TrayPalette palette)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);

        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

        using (var context = visual.RenderOpen())
        {
            double extent = size;
            var center = new Point(extent / 2, extent / 2);
            var thickness = Math.Max(1.5, extent / 8);
            var radius = (extent / 2) - (thickness / 2) - 0.5;

            context.DrawEllipse(null, Pen(palette.Track, thickness), center, radius, radius);

            if (state.Percent is { } percent && percent > 0)
            {
                var color = StatusColor(state.Status, palette);
                if (state.IsStale)
                {
                    color = Color.FromArgb(0x99, color.R, color.G, color.B);
                }

                var pen = Pen(color, thickness);
                var sweep = Math.Clamp(percent, 0, 100) / 100 * 360;
                if (sweep >= 359.5)
                {
                    context.DrawEllipse(null, pen, center, radius, radius);
                }
                else
                {
                    context.DrawGeometry(null, pen, ArcGeometry.Build(center, radius, -90, sweep));
                }
            }

            DrawNumeral(context, state.Text, extent, center, palette.Text);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawNumeral(DrawingContext context, string text, double extent, Point center, Color color)
    {
        var fontSize = text.Length >= 2 ? extent * 0.58 : extent * 0.72;
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            NumeralTypeface,
            fontSize,
            brush,
            pixelsPerDip: 1.0)
        {
            TextAlignment = TextAlignment.Center,
        };

        // Centre the ink, not the line box: digits have no descenders, so the line box sits too high.
        var inkBottom = formatted.Height + formatted.OverhangAfter;
        var inkTop = inkBottom - formatted.Extent;
        var originY = center.Y - ((inkTop + inkBottom) / 2);
        context.DrawText(formatted, new Point(center.X, originY));
    }

    private static Color StatusColor(UsageWindowStatus? status, TrayPalette palette) => status switch
    {
        UsageWindowStatus.Warning => palette.Warn,
        UsageWindowStatus.Critical or UsageWindowStatus.Exhausted => palette.Danger,
        _ => palette.Ok,
    };

    private static Pen Pen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

}
