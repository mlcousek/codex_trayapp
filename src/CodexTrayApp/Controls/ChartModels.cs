using System.Windows.Media;

namespace CodexTrayApp.Controls;

/// <summary>One point of a line: X is a DateTime tick count (or any monotonic number), Y the value.</summary>
public readonly record struct ChartPoint(double X, double Y)
{
    public bool BreakBefore { get; init; }
}

/// <summary>A named line with its brush; the name shows in hover text.</summary>
public sealed record ChartSeries(string Name, Brush Stroke, IReadOnlyList<ChartPoint> Points, string ValueSuffix = "%", bool Dashed = false);

/// <summary>A vertical marker line with a short label, for example the projected limit or the reset.</summary>
public sealed record ChartMarker(double X, string Label, Brush Stroke, bool Dashed);

/// <summary>One stacked segment across every category; the values array is parallel to the chart's categories.</summary>
public sealed record BarSeries(string Name, Brush Fill, IReadOnlyList<double> Values);
