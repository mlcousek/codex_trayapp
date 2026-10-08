using System.Windows;
using System.Windows.Media;

namespace CodexTrayApp.Controls;

/// <summary>A label-free line for a small space. Y is scaled to 0..MaxY; X spans MinX..MaxX or the points themselves.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(
        nameof(Baseline), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxYProperty = DependencyProperty.Register(
        nameof(MaxY), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinXProperty = DependencyProperty.Register(
        nameof(MinX), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxXProperty = DependencyProperty.Register(
        nameof(MaxX), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Baseline
    {
        get => (Brush?)GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    public double MaxY
    {
        get => (double)GetValue(MaxYProperty);
        set => SetValue(MaxYProperty, value);
    }

    /// <summary>Left edge of the time axis; NaN means the first point.</summary>
    public double MinX
    {
        get => (double)GetValue(MinXProperty);
        set => SetValue(MinXProperty, value);
    }

    /// <summary>Right edge of the time axis; NaN means the last point.</summary>
    public double MaxX
    {
        get => (double)GetValue(MaxXProperty);
        set => SetValue(MaxXProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Baseline is { } baseline)
        {
            drawingContext.DrawLine(new Pen(baseline, 1), new Point(0, height - 0.5), new Point(width, height - 0.5));
        }

        var points = Points;
        if (points is null || points.Count < 2 || Stroke is not { } stroke)
        {
            return;
        }

        var minX = double.IsNaN(MinX) ? points[0].X : MinX;
        var maxX = double.IsNaN(MaxX) ? points[^1].X : MaxX;
        var geometry = ChartGeometry.Polyline(points, minX, maxX, 0, Math.Max(MaxY, 1), width, height - 1, 1);
        drawingContext.DrawGeometry(null, new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round }, geometry);
    }
}

/// <summary>Maps data to pixels for the hand-drawn charts.</summary>
internal static class ChartGeometry
{
    public static double ToX(double x, double minX, double maxX, double width) =>
        maxX <= minX ? 0 : (x - minX) / (maxX - minX) * width;

    public static double ToY(double y, double minY, double maxY, double height, double top) =>
        maxY <= minY ? top + height : top + height - ((Math.Clamp(y, minY, maxY) - minY) / (maxY - minY) * height);

    public static StreamGeometry Polyline(IReadOnlyList<ChartPoint> points, double minX, double maxX, double minY, double maxY, double width, double height, double top)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var point = new Point(ToX(points[i].X, minX, maxX, width), ToY(points[i].Y, minY, maxY, height, top));
                if (i == 0 || points[i].BreakBefore)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
