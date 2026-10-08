using CodexTrayApp.Tray;
using Shouldly;

namespace CodexTrayApp.Tests;

public class FlyoutPlacementTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void Bottom_taskbar_puts_the_flyout_above_it_centred_on_the_anchor()
    {
        var work = new PixelRect(0, 0, 2560, 1392);

        var (x, y, edge) = FlyoutPlacement.Compute(Monitor, work, anchorX: 2400, anchorY: 1420, width: 400, height: 300, margin: 12);

        edge.ShouldBe(TaskbarEdge.Bottom);
        y.ShouldBe(1392 - 300 - 12);
        x.ShouldBe(2560 - 400 - 12);
    }

    [Fact]
    public void Top_taskbar_puts_the_flyout_below_it()
    {
        var work = new PixelRect(0, 48, 2560, 1440);

        var (x, y, edge) = FlyoutPlacement.Compute(Monitor, work, 1280, 20, 400, 300, 12);

        edge.ShouldBe(TaskbarEdge.Top);
        y.ShouldBe(48 + 12);
        x.ShouldBe(1280 - 200);
    }

    [Fact]
    public void Left_taskbar_puts_the_flyout_beside_it()
    {
        var work = new PixelRect(64, 0, 2560, 1440);

        var (x, y, edge) = FlyoutPlacement.Compute(Monitor, work, 30, 1400, 400, 300, 12);

        edge.ShouldBe(TaskbarEdge.Left);
        x.ShouldBe(64 + 12);
        y.ShouldBe(1440 - 300 - 12);
    }

    [Fact]
    public void Right_taskbar_puts_the_flyout_beside_it()
    {
        var work = new PixelRect(0, 0, 2496, 1440);

        var (x, y, edge) = FlyoutPlacement.Compute(Monitor, work, 2540, 700, 400, 300, 12);

        edge.ShouldBe(TaskbarEdge.Right);
        x.ShouldBe(2496 - 400 - 12);
        y.ShouldBe(700 - 150);
    }

    [Fact]
    public void An_auto_hidden_taskbar_is_treated_as_bottom()
    {
        FlyoutPlacement.DetectEdge(Monitor, Monitor).ShouldBe(TaskbarEdge.Bottom);
    }

    [Fact]
    public void Secondary_monitors_with_negative_coordinates_clamp_correctly()
    {
        var monitor = new PixelRect(-1920, 200, 0, 1280);
        var work = new PixelRect(-1920, 200, 0, 1232);

        var (x, y, _) = FlyoutPlacement.Compute(monitor, work, -1900, 1260, 400, 300, 12);

        x.ShouldBe(-1920 + 12);
        y.ShouldBe(1232 - 300 - 12);
    }

    [Fact]
    public void A_flyout_taller_than_the_work_area_still_gets_a_position()
    {
        var work = new PixelRect(0, 0, 2560, 1392);

        var (_, y, _) = FlyoutPlacement.Compute(Monitor, work, 100, 100, 400, 5000, 12);

        y.ShouldBe(12);
    }
}
