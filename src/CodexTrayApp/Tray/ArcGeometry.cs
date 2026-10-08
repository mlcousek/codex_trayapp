using System.Windows;
using System.Windows.Media;

namespace CodexTrayApp.Tray;

/// <summary>Arc geometry shared by the tray icon and the flyout ring: clockwise from a start angle, degrees.</summary>
internal static class ArcGeometry
{
    public static PathGeometry Build(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var figure = new PathFigure
        {
            StartPoint = PointOn(center, radius, startDegrees),
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new ArcSegment(
            PointOn(center, radius, startDegrees + sweepDegrees),
            new Size(radius, radius),
            0,
            sweepDegrees > 180,
            SweepDirection.Clockwise,
            true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    public static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }
}
