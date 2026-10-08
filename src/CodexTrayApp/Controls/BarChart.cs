using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexTrayApp.Controls;

/// <summary>
/// Stacked columns per category with a hairline baseline, first and last category labels, the top value, and a
/// hover readout. Drawn directly so it follows the theme brushes it is given.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    private const double LabelHeight = 14;
    private const double TopPadding = 14;
    private const double Gap = 3;

    public static readonly DependencyProperty SeriesProperty = Register(nameof(Series), typeof(IReadOnlyList<BarSeries>));
    public static readonly DependencyProperty CategoriesProperty = Register(nameof(Categories), typeof(IReadOnlyList<string>));
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush), typeof(Brush));
    public static readonly DependencyProperty BaselineBrushProperty = Register(nameof(BaselineBrush), typeof(Brush));
    public static readonly DependencyProperty HoverBackgroundProperty = Register(nameof(HoverBackground), typeof(Brush));
    public static readonly DependencyProperty HoverForegroundProperty = Register(nameof(HoverForeground), typeof(Brush));
    public static readonly DependencyProperty ValueFormatterProperty = Register(nameof(ValueFormatter), typeof(Func<double, string>));

    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private Point? _hover;

    public IReadOnlyList<BarSeries>? Series
    {
        get => (IReadOnlyList<BarSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IReadOnlyList<string>? Categories
    {
        get => (IReadOnlyList<string>?)GetValue(CategoriesProperty);
        set => SetValue(CategoriesProperty, value);
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

    public Func<double, string>? ValueFormatter
    {
        get => (Func<double, string>?)GetValue(ValueFormatterProperty);
        set => SetValue(ValueFormatterProperty, value);
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

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));
        var baselineY = TopPadding + plotHeight;
        if (BaselineBrush is { } baseline)
        {
            drawingContext.DrawLine(new Pen(baseline, 1), new Point(0, baselineY + 0.5), new Point(width, baselineY + 0.5));
        }

        var categories = Categories;
        var series = Series;
        if (categories is not { Count: > 0 } || series is not { Count: > 0 })
        {
            return;
        }

        var totals = new double[categories.Count];
        foreach (var bar in series)
        {
            for (var i = 0; i < categories.Count && i < bar.Values.Count; i++)
            {
                totals[i] += Math.Max(0, bar.Values[i]);
            }
        }

        var max = Math.Max(totals.Max(), 1);
        var slot = width / categories.Count;
        var barWidth = Math.Max(1, slot - Gap);
        var format = ValueFormatter ?? (v => v.ToString("0", CultureInfo.CurrentCulture));

        for (var i = 0; i < categories.Count; i++)
        {
            var x = (i * slot) + (Gap / 2);
            var top = baselineY;
            foreach (var bar in series)
            {
                var value = i < bar.Values.Count ? Math.Max(0, bar.Values[i]) : 0;
                if (value <= 0)
                {
                    continue;
                }

                var segment = value / max * plotHeight;
                top -= segment;
                drawingContext.DrawRectangle(bar.Fill, null, new Rect(x, top, barWidth, segment));
            }
        }

        if (LabelBrush is { } labelBrush)
        {
            drawingContext.DrawText(Text(categories[0], labelBrush), new Point(0, baselineY + 2));
            var last = Text(categories[^1], labelBrush);
            drawingContext.DrawText(last, new Point(width - last.Width, baselineY + 2));
            drawingContext.DrawText(Text(format(max), labelBrush), new Point(0, 0));
        }

        if (_hover is { } hover && HoverForeground is { } foreground)
        {
            var index = Math.Clamp((int)(hover.X / slot), 0, categories.Count - 1);
            var parts = series.Count > 1
                ? string.Join(" · ", series.Where(s => index < s.Values.Count && s.Values[index] > 0).Select(s => s.Name + " " + format(s.Values[index])))
                : string.Empty;
            var label = Text($"{categories[index]}: {format(totals[index])}" + (parts.Length > 0 ? " (" + parts + ")" : string.Empty), foreground);
            var boxWidth = Math.Min(label.Width + 10, width);
            var boxX = Math.Clamp((index * slot) + (slot / 2) - (boxWidth / 2), 0, Math.Max(0, width - boxWidth));
            drawingContext.DrawRoundedRectangle(HoverBackground, null, new Rect(boxX, 0, boxWidth, 16), 4, 4);
            drawingContext.DrawText(label, new Point(boxX + 5, 1));
        }
    }

    private static DependencyProperty Register(string name, Type type) =>
        DependencyProperty.Register(name, type, typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private FormattedText Text(string text, Brush brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        LabelTypeface,
        10,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
