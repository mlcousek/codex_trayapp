using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTrayApp.Tray;
using Shouldly;
using WpfBrushes = System.Windows.Media.Brushes;

namespace CodexTrayApp.Tests;

/// <summary>The hop from a rendered WPF bitmap to the GDI icon the shell shows: size and colour must survive it.</summary>
public class IconConverterTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void A_rendered_bitmap_becomes_an_icon_of_the_same_size_and_colour(int size)
    {
        var (width, height, pixel) = StaThread.Run(() =>
        {
            using var icon = IconConverter.ToIcon(Render(size, WpfBrushes.Red));
            using var bitmap = icon.ToBitmap();
            return (icon.Width, icon.Height, bitmap.GetPixel(size / 2, size / 2));
        });

        width.ShouldBe(size);
        height.ShouldBe(size);
        pixel.A.ShouldBeGreaterThan((byte)200);
        pixel.R.ShouldBeGreaterThan((byte)200);
        pixel.G.ShouldBeLessThan((byte)50);
        pixel.B.ShouldBeLessThan((byte)50);
    }

    [Fact]
    public void Transparent_pixels_stay_transparent()
    {
        var alpha = StaThread.Run(() =>
        {
            using var icon = IconConverter.ToIcon(Render(16, WpfBrushes.Transparent));
            using var bitmap = icon.ToBitmap();
            return bitmap.GetPixel(8, 8).A;
        });

        alpha.ShouldBe((byte)0);
    }

    [Fact]
    public void A_missing_bitmap_is_refused() =>
        Should.Throw<ArgumentNullException>(() => IconConverter.ToIcon(null!));

    private static RenderTargetBitmap Render(int size, Brush fill)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(fill, null, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
