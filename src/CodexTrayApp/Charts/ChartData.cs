namespace CodexTrayApp.Charts;

/// <summary>One value at one instant; the raw material of every line drawn in the flyout.</summary>
public readonly record struct TimePoint(DateTimeOffset At, double Value)
{
    public bool BreakBefore { get; init; }
}

/// <summary>A line identified by a stable key (a window key, a model id) with its display name.</summary>
public sealed record TimeSeries(string Key, string Name, IReadOnlyList<TimePoint> Points);

/// <summary>Utilization of every window over a range. <see cref="Summary"/> is the text equivalent for screen readers and captions.</summary>
public sealed record HistoryChart(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<TimeSeries> Series, string Summary)
{
    public bool HasData => Series.Any(s => s.Points.Count >= 2);
}

/// <summary>
/// The current 5-hour block: recorded percentages since the block began, a dashed projection to the limit when the pace
/// is known, and the reset at the right edge. <see cref="LimitAt"/> is set only when the projected limit lands inside
/// the block, so the view can mark it; <see cref="LimitBeforeReset"/> repeats the calculator's verdict.
/// </summary>
public sealed record BurnChart(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TimePoint> Recorded,
    IReadOnlyList<TimePoint> Projection,
    DateTimeOffset? LimitAt,
    bool LimitBeforeReset,
    string Summary)
{
    public bool HasData => Recorded.Count >= 2;
}

/// <summary>Tokens per local day for the last N days, split by model with the long tail grouped as "other".</summary>
public sealed record DailyChart(
    IReadOnlyList<DateOnly> Days,
    IReadOnlyList<string> Labels,
    IReadOnlyList<double> Totals,
    IReadOnlyList<DailySeries> ByModel,
    string Summary)
{
    public bool HasData => Totals.Any(t => t > 0);
}

/// <summary>One model's tokens per day, parallel to <see cref="DailyChart.Days"/>.</summary>
public sealed record DailySeries(string Model, IReadOnlyList<double> Values);

/// <summary>A window's percentages over the period its sparkline covers.</summary>
public sealed record SparkData(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<TimePoint> Points);

/// <summary>Everything the charts need for one refresh, loaded off the UI thread in one go.</summary>
public sealed record ChartBundle(
    HistoryChart History,
    BurnChart? Block,
    DailyChart Daily,
    IReadOnlyDictionary<string, SparkData> Sparklines);
