using System.Windows;
using System.Windows.Media;
using CodexTrayApp.Tray;

namespace CodexTrayApp.Controls;

/// <summary>
/// A ring-arc progress indicator drawn directly, so it stays crisp at any DPI. Percent is a dependency property
/// and can be animated; the arc starts at twelve o'clock and runs clockwise.
/// </summary>
public sealed class RingArc : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(RingArc), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingArc), new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingArc), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(RingArc), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = Math.Max(1, Thickness);
        var radius = (size / 2) - (thickness / 2);

        if (Track is { } track)
        {
            drawingContext.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        }

        var sweep = Math.Clamp(double.IsFinite(Percent) ? Percent : 0, 0, 100) / 100 * 360;
        if (Stroke is not { } stroke || sweep <= 0)
        {
            return;
        }

        var pen = new Pen(stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (sweep >= 359.9)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
        }
        else
        {
            drawingContext.DrawGeometry(null, pen, ArcGeometry.Build(center, radius, -90, sweep));
        }
    }
}
