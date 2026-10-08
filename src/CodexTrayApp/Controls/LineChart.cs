using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTrayApp.Controls;

/// <summary>
/// A minimal multi-series line chart: one hairline baseline, two edge labels on X, the top value on Y, optional
/// vertical markers, and a hover readout. Everything is drawn directly, so it follows the theme brushes it is given.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private const double LabelHeight = 14;
    private const double TopPadding = 6;

    public static readonly DependencyProperty SeriesProperty = Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>));
    public static readonly DependencyProperty MarkersProperty = Register(nameof(Markers), typeof(IReadOnlyList<ChartMarker>));
    public static readonly DependencyProperty MinXProperty = Register(nameof(MinX), typeof(double), 0.0);
    public static readonly DependencyProperty MaxXProperty = Register(nameof(MaxX), typeof(double), 1.0);
    public static readonly DependencyProperty MaxYProperty = Register(nameof(MaxY), typeof(double), 100.0);
    public static readonly DependencyProperty StartLabelProperty = Register(nameof(StartLabel), typeof(string));
    public static readonly DependencyProperty EndLabelProperty = Register(nameof(EndLabel), typeof(string));
    public static readonly DependencyProperty TopLabelProperty = Register(nameof(TopLabel), typeof(string));
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush), typeof(Brush));
    public static readonly DependencyProperty BaselineBrushProperty = Register(nameof(BaselineBrush), typeof(Brush));
    public static readonly DependencyProperty HoverBackgroundProperty = Register(nameof(HoverBackground), typeof(Brush));
    public static readonly DependencyProperty HoverForegroundProperty = Register(nameof(HoverForeground), typeof(Brush));
    public static readonly DependencyProperty XLabelerProperty = Register(nameof(XLabeler), typeof(Func<double, string>));

    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private Point? _hover;

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<ChartMarker>? Markers
    {
        get => (IReadOnlyList<ChartMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public double MinX
    {
        get => (double)GetValue(MinXProperty);
        set => SetValue(MinXProperty, value);
    }

    public double MaxX
    {
        get => (double)GetValue(MaxXProperty);
        set => SetValue(MaxXProperty, value);
    }

    public double MaxY
    {
        get => (double)GetValue(MaxYProperty);
        set => SetValue(MaxYProperty, value);
    }

    public string? StartLabel
    {
        get => (string?)GetValue(StartLabelProperty);
        set => SetValue(StartLabelProperty, value);
    }

    public string? EndLabel
    {
        get => (string?)GetValue(EndLabelProperty);
        set => SetValue(EndLabelProperty, value);
    }

    public string? TopLabel
    {
        get => (string?)GetValue(TopLabelProperty);
        set => SetValue(TopLabelProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush? BaselineBrush
    {
        get => (Brush?)GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public Brush? HoverBackground
    {
        get => (Brush?)GetValue(HoverBackgroundProperty);
        set => SetValue(HoverBackgroundProperty, value);
    }

    public Brush? HoverForeground
    {
        get => (Brush?)GetValue(HoverForegroundProperty);
        set => SetValue(HoverForegroundProperty, value);
    }

    /// <summary>Formats an X value (DateTime ticks by convention) for the hover readout.</summary>
    public Func<double, string>? XLabeler
    {
        get => (Func<double, string>?)GetValue(XLabelerProperty);
        set => SetValue(XLabelerProperty, value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var plotHeight = ActualHeight - LabelHeight - TopPadding;
        if (width <= 0 || plotHeight <= 0)
        {
            return;
        }

        // A transparent fill so the element receives mouse events over its whole area.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        var baselineY = TopPadding + plotHeight;
        if (BaselineBrush is { } baseline)
        {
            drawingContext.DrawLine(new Pen(baseline, 1), new Point(0, baselineY + 0.5), new Point(width, baselineY + 0.5));
        }

        DrawLabels(drawingContext, width, baselineY);
        DrawMarkers(drawingContext, width, plotHeight);

        var series = Series;
        if (series is null)
        {
            return;
        }

        foreach (var line in series)
        {
            if (line.Points.Count < 2)
            {
                continue;
            }

            var geometry = ChartGeometry.Polyline(line.Points, MinX, MaxX, 0, Math.Max(MaxY, 1), width, plotHeight, TopPadding);
            var pen = new Pen(line.Stroke, 1.5) { LineJoin = PenLineJoin.Round };
            if (line.Dashed)
            {
                pen.DashStyle = new DashStyle([3, 3], 0);
            }

            drawingContext.DrawGeometry(null, pen, geometry);
        }

        DrawHover(drawingContext, series, width, plotHeight);
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue = null) =>
        DependencyProperty.Register(name, type, typeof(LineChart), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private FormattedText Text(string text, Brush brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        LabelTypeface,
        10,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawLabels(DrawingContext drawingContext, double width, double baselineY)
    {
        if (LabelBrush is not { } brush)
        {
            return;
        }

        if (StartLabel is { Length: > 0 } start)
        {
            drawingContext.DrawText(Text(start, brush), new Point(0, baselineY + 2));
        }

        if (EndLabel is { Length: > 0 } end)
        {
            var formatted = Text(end, brush);
            drawingContext.DrawText(formatted, new Point(width - formatted.Width, baselineY + 2));
        }

        if (TopLabel is { Length: > 0 } top)
        {
            drawingContext.DrawText(Text(top, brush), new Point(0, 0));
        }
    }

    private void DrawMarkers(DrawingContext drawingContext, double width, double plotHeight)
    {
        if (Markers is not { Count: > 0 } markers)
        {
            return;
        }

        foreach (var marker in markers)
        {
            var x = Math.Round(ChartGeometry.ToX(marker.X, MinX, MaxX, width)) + 0.5;
            if (x < 0 || x > width)
            {
                continue;
            }

            var pen = new Pen(marker.Stroke, 1);
            if (marker.Dashed)
            {
                pen.DashStyle = new DashStyle([3, 3], 0);
            }

            drawingContext.DrawLine(pen, new Point(x, TopPadding), new Point(x, TopPadding + plotHeight));
            if (marker.Label.Length > 0)
            {
                var text = Text(marker.Label, marker.Stroke);
                var textX = x + 4 + text.Width > width ? x - 4 - text.Width : x + 4;
                drawingContext.DrawText(text, new Point(textX, TopPadding));
            }
        }
    }

    private void DrawHover(DrawingContext drawingContext, IReadOnlyList<ChartSeries> series, double width, double plotHeight)
    {
        if (_hover is not { } hover || HoverForeground is not { } foreground)
        {
            return;
        }

        var dataX = MinX + (hover.X / width * (MaxX - MinX));
        ChartSeries? bestSeries = null;
        ChartPoint? best = null;
        foreach (var line in series)
        {
            foreach (var point in line.Points)
            {
                if (best is null || Math.Abs(point.X - dataX) < Math.Abs(best.Value.X - dataX))
                {
                    best = point;
                    bestSeries = line;
                }
            }
        }

        if (best is not { } nearest || bestSeries is null)
        {
            return;
        }

        var x = ChartGeometry.ToX(nearest.X, MinX, MaxX, width);
        var y = ChartGeometry.ToY(nearest.Y, 0, Math.Max(MaxY, 1), plotHeight, TopPadding);
        drawingContext.DrawLine(new Pen(bestSeries.Stroke, 1) { DashStyle = DashStyles.Dot }, new Point(x, TopPadding), new Point(x, TopPadding + plotHeight));
        drawingContext.DrawEllipse(bestSeries.Stroke, null, new Point(x, y), 2.5, 2.5);

        var when = XLabeler?.Invoke(nearest.X) ?? string.Empty;
        var value = nearest.Y.ToString("0.#", CultureInfo.CurrentCulture) + bestSeries.ValueSuffix;
        var label = Text(string.IsNullOrEmpty(when) ? $"{bestSeries.Name} {value}" : $"{when} · {bestSeries.Name} {value}", foreground);
        var boxWidth = label.Width + 10;
        var boxX = Math.Clamp(x - (boxWidth / 2), 0, Math.Max(0, width - boxWidth));
        var boxY = Math.Max(0, y - 22);
        drawingContext.DrawRoundedRectangle(HoverBackground, null, new Rect(boxX, boxY, boxWidth, 16), 4, 4);
        drawingContext.DrawText(label, new Point(boxX + 5, boxY + 1));
    }
}
