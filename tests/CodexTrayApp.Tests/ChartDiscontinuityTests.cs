using CodexTrayApp.Controls;
using System.Windows;
using System.Windows.Media;
using Shouldly;

namespace CodexTrayApp.Tests;

public class ChartDiscontinuityTests
{
    [Fact]
    public void Gaps_are_separate_figures_in_the_rendered_geometry() => StaThread.Run(() =>
    {
        ChartPoint[] points = [new(0, 10), new(1, 20), new(2, 5) { BreakBefore = true }, new(3, 10)];
        var geometry = ChartGeometry.Polyline(points, 0, 3, 0, 100, 300, 100, 0);
        var pen = new Pen(Brushes.Black, 2);
        geometry.StrokeContains(pen, new Point(50, 85)).ShouldBeTrue();
        geometry.StrokeContains(pen, new Point(250, 92.5)).ShouldBeTrue();
        geometry.StrokeContains(pen, new Point(150, 87.5)).ShouldBeFalse();
        return true;
    });
}
