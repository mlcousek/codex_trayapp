using System.Windows;
using System.Windows.Media;
using CodexTrayApp.Controls;
using CodexTrayApp.Tray;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>
/// The arithmetic under every ring and chart: angles to points (clockwise, because screen y grows downwards) and
/// data to pixels, including the degenerate ranges an empty or single-reading chart produces.
/// </summary>
public class GeometryTests
{
    private const double Tolerance = 1e-9;
    private static readonly Point Center = new(10, 10);

    [Theory]
    [InlineData(0, 15, 10)]
    [InlineData(90, 10, 15)]
    [InlineData(180, 5, 10)]
    [InlineData(270, 10, 5)]
    [InlineData(-90, 10, 5)]
    public void Angles_map_clockwise_from_three_o_clock(double degrees, double x, double y)
    {
        var point = ArcGeometry.PointOn(Center, 5, degrees);

        point.X.ShouldBe(x, Tolerance);
        point.Y.ShouldBe(y, Tolerance);
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(181, true)]
    [InlineData(359.9, true)]
    public void Only_a_sweep_past_half_a_turn_is_a_large_arc(double sweep, bool large) =>
        StaThread.Run(() => Arc(ArcGeometry.Build(Center, 5, -90, sweep)).IsLargeArc).ShouldBe(large);

    [Fact]
    public void An_arc_runs_clockwise_from_its_start_to_start_plus_sweep_and_is_frozen()
    {
        var (start, end, size, direction, frozen, filled) = StaThread.Run(() =>
        {
            var geometry = ArcGeometry.Build(Center, 5, -90, 90);
            var figure = geometry.Figures[0];
            var arc = Arc(geometry);
            return (figure.StartPoint, arc.Point, arc.Size, arc.SweepDirection, geometry.IsFrozen, figure.IsFilled);
        });

        start.X.ShouldBe(10, Tolerance);
        start.Y.ShouldBe(5, Tolerance);
        end.X.ShouldBe(15, Tolerance);
        end.Y.ShouldBe(10, Tolerance);
        size.ShouldBe(new Size(5, 5));
        direction.ShouldBe(SweepDirection.Clockwise);
        frozen.ShouldBeTrue();
        filled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 50)]
    [InlineData(10, 100)]
    [InlineData(-5, -50)]
    public void X_scales_linearly_across_the_width(double x, double expected) =>
        ChartGeometry.ToX(x, 0, 10, 100).ShouldBe(expected, Tolerance);

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    public void A_collapsed_x_range_puts_everything_at_the_left_edge(double minX, double maxX) =>
        ChartGeometry.ToX(7, minX, maxX, 100).ShouldBe(0);

    [Theory]
    [InlineData(0, 60)]
    [InlineData(50, 35)]
    [InlineData(100, 10)]
    [InlineData(150, 10)]
    [InlineData(-20, 60)]
    public void Y_grows_upwards_from_the_baseline_and_is_clamped_to_the_plot(double y, double expected) =>
        ChartGeometry.ToY(y, 0, 100, 50, 10).ShouldBe(expected, Tolerance);

    [Fact]
    public void A_collapsed_y_range_sits_on_the_baseline() =>
        ChartGeometry.ToY(42, 100, 100, 50, 10).ShouldBe(60);

    [Fact]
    public void A_polyline_covers_exactly_the_mapped_points()
    {
        var bounds = StaThread.Run(() =>
        {
            var geometry = ChartGeometry.Polyline([new ChartPoint(0, 0), new ChartPoint(5, 50), new ChartPoint(10, 100)], 0, 10, 0, 100, 100, 50, 10);
            geometry.IsFrozen.ShouldBeTrue();
            return geometry.Bounds;
        });

        bounds.Left.ShouldBe(0, Tolerance);
        bounds.Top.ShouldBe(10, Tolerance);
        bounds.Width.ShouldBe(100, Tolerance);
        bounds.Height.ShouldBe(50, Tolerance);
    }

    [Fact]
    public void A_polyline_of_no_points_is_empty() =>
        StaThread.Run(() => ChartGeometry.Polyline([], 0, 10, 0, 100, 100, 50, 10).IsEmpty()).ShouldBeTrue();

    private static ArcSegment Arc(PathGeometry geometry) => geometry.Figures[0].Segments[0].ShouldBeOfType<ArcSegment>();
}
