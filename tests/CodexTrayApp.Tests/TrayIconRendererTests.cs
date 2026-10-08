using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Tray;
using Shouldly;

namespace CodexTrayApp.Tests;

public class TrayIconRendererTests
{
    private static readonly TrayPalette Palette = new(
        Colors.White,
        Color.FromRgb(0x7B, 0xA0, 0x5B),
        Color.FromRgb(0xD4, 0xA2, 0x4C),
        Color.FromRgb(0xC1, 0x55, 0x4A));

    private static BitmapSource Render(TrayIconState state, int size = 32) =>
        StaThread.Run(() => TrayIconRenderer.Render(state, size, Palette));

    /// <summary>BGRA of one pixel (premultiplied alpha).</summary>
    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bitmap, int x, int y)
    {
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Renders_at_the_exact_pixel_size(int size)
    {
        var bitmap = Render(new TrayIconState(45, UsageWindowStatus.Ok, false), size);

        bitmap.PixelWidth.ShouldBe(size);
        bitmap.PixelHeight.ShouldBe(size);
        bitmap.IsFrozen.ShouldBeTrue();
    }

    [Fact]
    public void The_arc_uses_the_status_colour_and_starts_at_twelve_o_clock()
    {
        // 45 %: the arc covers the top and the right side but not the left.
        var bitmap = Render(new TrayIconState(45, UsageWindowStatus.Ok, false));

        var top = Pixel(bitmap, 16, 2);
        var right = Pixel(bitmap, 29, 16);
        var left = Pixel(bitmap, 2, 16);

        top.A.ShouldBeGreaterThan((byte)200);
        top.G.ShouldBeGreaterThan(top.R);
        right.G.ShouldBeGreaterThan(right.R);
        left.A.ShouldBeLessThan((byte)128);
    }

    [Fact]
    public void Critical_windows_draw_a_red_arc()
    {
        var top = Pixel(Render(new TrayIconState(93, UsageWindowStatus.Critical, false)), 16, 2);

        top.R.ShouldBeGreaterThan(top.G);
        top.R.ShouldBeGreaterThan(top.B);
    }

    [Fact]
    public void Stale_data_draws_a_translucent_arc()
    {
        var fresh = Pixel(Render(new TrayIconState(93, UsageWindowStatus.Critical, false)), 16, 2);
        var stale = Pixel(Render(new TrayIconState(93, UsageWindowStatus.Critical, true)), 16, 2);

        stale.A.ShouldBeLessThan(fresh.A);
        stale.A.ShouldBeGreaterThan((byte)100);
    }

    [Fact]
    public void Unknown_state_has_only_the_faint_track()
    {
        var top = Pixel(Render(TrayIconState.Unknown), 16, 2);

        top.A.ShouldBeLessThan((byte)128);
    }
}
