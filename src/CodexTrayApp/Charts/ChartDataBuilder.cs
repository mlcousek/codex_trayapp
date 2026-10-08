using System.Globalization;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Diagnostics;

namespace CodexTrayApp.Charts;

/// <summary>
/// Pure functions from stored rows to chart data. No WPF types and no clock, so every rule here is unit-tested.
/// Percentages come from recorded snapshots only; nothing here invents a value the endpoint did not report.
/// </summary>
public static class ChartDataBuilder
{
    public const int MaxPointsPerLine = 240;
    public const int MaxSparklinePoints = 80;
    public const int DailyDays = 14;
    public const int MaxDailyModels = 4;
    public const string OtherModels = "other";
    // Three missed default polls: do not interpolate through extended offline/backoff periods.
    public static readonly TimeSpan MaximumConnectedGap = TimeSpan.FromMinutes(15);

    /// <summary>One line per window over [from, to]. Windows without rows are left out; the caller decides the order.</summary>
    public static HistoryChart History(
        IReadOnlyList<(string Key, string Name, IReadOnlyList<HistoryPoint> Points)> windows,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var series = windows
            .Where(w => w.Points.Count > 0)
            .Select(w => new TimeSeries(w.Key, w.Name, Downsample(ToPoints(w.Points), from, to, MaxPointsPerLine)))
            .ToList();

        var summary = series.Count == 0
            ? "No history recorded yet. Lines appear after a few refreshes."
            : "Last " + DescribeRange(to - from) + ": " + string.Join("; ", series.Select(s => s.Name + " " + Describe(s.Points))) + ".";
        return new HistoryChart(from, to, series, summary);
    }

    /// <summary>
    /// The 5-hour block's recorded percentages plus a straight projection from the current value at the calculator's
    /// pace. The projection stops at the block's end; when the limit would land later than the reset, the line simply
    /// ends short of 100.
    /// </summary>
    public static BurnChart Burn(
        IReadOnlyList<HistoryPoint> fiveHour,
        BlockAnalytics block,
        double? currentPercent,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(fiveHour);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(zone);

        var recorded = ToPoints(fiveHour.Where(p => p.Timestamp >= block.Start && p.Timestamp <= now));
        if (currentPercent is { } current && (recorded.Count == 0 || recorded[^1].At < now))
        {
            recorded.Add(new TimePoint(now, current)
            {
                BreakBefore = recorded.Count > 0 && (now - recorded[^1].At > MaximumConnectedGap || current < recorded[^1].Value),
            });
        }

        var downsampled = Downsample(recorded, block.Start, block.End, MaxPointsPerLine);

        var projection = new List<TimePoint>();
        DateTimeOffset? limitAt = null;
        if (block.PercentPerHour is > 0 and var rate && block.ProjectedLimitAt is { } projected && projected > now && currentPercent is { } percent && percent < 100)
        {
            var insideBlock = projected <= block.End;
            var end = insideBlock ? projected : block.End;
            var endValue = insideBlock ? 100 : Math.Min(100, percent + (rate * (end - now).TotalHours));
            projection.Add(new TimePoint(now, percent));
            projection.Add(new TimePoint(end, endValue));
            limitAt = insideBlock ? projected : null;
        }

        var summary = BurnSummary(block, downsampled, now, zone);
        return new BurnChart(block.Start, block.End, downsampled, projection, limitAt, block.LimitBeforeReset, summary);
    }

    /// <summary>Tokens per local day ending today, the top models kept apart and the rest grouped as "other".</summary>
    public static DailyChart Daily(IReadOnlyList<DailyModelTotals> totals, DateOnly today, int days = DailyDays, int maxModels = MaxDailyModels)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);

        var first = today.AddDays(-(days - 1));
        var dayList = Enumerable.Range(0, days).Select(first.AddDays).ToList();
        var index = new Dictionary<DateOnly, int>();
        for (var i = 0; i < dayList.Count; i++)
        {
            index[dayList[i]] = i;
        }

        var inRange = totals.Where(t => index.ContainsKey(t.Day)).ToList();
        var ranked = inRange
            .GroupBy(t => t.Model, StringComparer.Ordinal)
            .Select(g => (Model: g.Key, Total: g.Sum(t => t.Tokens.Total), Rows: g.ToList()))
            .OrderByDescending(m => m.Total)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToList();

        var byModel = new List<DailySeries>();
        foreach (var model in ranked.Take(maxModels))
        {
            byModel.Add(new DailySeries(model.Model, Values(model.Rows, index, days)));
        }

        var rest = ranked.Skip(maxModels).SelectMany(m => m.Rows).ToList();
        if (rest.Count > 0)
        {
            byModel.Add(new DailySeries(OtherModels, Values(rest, index, days)));
        }

        var dayTotals = Values(inRange, index, days);
        var labels = dayList.Select(d => d == today ? "Today" : d.ToString("MMM d", CultureInfo.CurrentCulture)).ToList();
        return new DailyChart(dayList, labels, dayTotals, byModel, DailySummary(dayTotals, labels, days));
    }

    public static IReadOnlyList<TimePoint> Sparkline(IReadOnlyList<HistoryPoint> points, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(points);
        return Downsample(ToPoints(points.Where(p => p.Timestamp >= from && p.Timestamp <= to)), from, to, MaxSparklinePoints);
    }

    /// <summary>"codex-sonnet-4-5-20250929" becomes "sonnet-4-5"; ids without the vendor prefix or a date are returned unchanged.</summary>
    public static string ShortModelName(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        const string Prefix = "codex-";
        var name = model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? model[Prefix.Length..] : model;
        if (name.Length > 9 && name[^9] == '-' && name[^8..].All(char.IsAsciiDigit))
        {
            name = name[..^9];
        }

        return name.Length == 0 ? model : name;
    }

    /// <summary>
    /// Keeps peak values in <paramref name="maxPoints"/> time buckets, plus endpoints and both sides of gaps.
    /// </summary>
    internal static IReadOnlyList<TimePoint> Downsample(IReadOnlyList<TimePoint> points, DateTimeOffset from, DateTimeOffset to, int maxPoints)
    {
        if (points.Count <= maxPoints || to <= from || maxPoints < 2)
        {
            return points;
        }

        var span = (to - from).Ticks;
        var buckets = new TimePoint?[maxPoints];
        foreach (var point in points)
        {
            var index = (int)Math.Clamp((point.At - from).Ticks * maxPoints / span, 0, maxPoints - 1);
            if (buckets[index] is not { } kept || point.Value >= kept.Value)
            {
                buckets[index] = point;
            }
        }

        var result = new List<TimePoint>(maxPoints + 2);
        var kept0 = buckets.First(b => b is not null)!.Value;
        if (kept0.At != points[0].At)
        {
            result.Add(points[0]);
        }

        result.AddRange(buckets.Where(b => b is not null).Select(b => b!.Value));
        if (result[^1].At != points[^1].At)
        {
            result.Add(points[^1]);
        }

        // Preserve both sides of discontinuities even when downsampling drops their bucket.
        for (var i = 1; i < points.Count; i++)
        {
            if (!points[i].BreakBefore) { continue; }
            result.Add(points[i - 1]);
            result.Add(points[i]);
        }
        return result.DistinctBy(p => p.At).OrderBy(p => p.At).ToList();
    }

    private static List<TimePoint> ToPoints(IEnumerable<HistoryPoint> points)
    {
        var result = new List<TimePoint>();
        HistoryPoint? previous = null;
        foreach (var point in points.OrderBy(p => p.Timestamp))
        {
            var discontinuity = previous is not null && (point.Timestamp - previous.Timestamp > MaximumConnectedGap
                || point.Percent < previous.Percent || point.ResetsAt != previous.ResetsAt
                || previous.ResetsAt is { } reset && point.Timestamp >= reset && previous.Timestamp < reset);
            result.Add(new TimePoint(point.Timestamp, point.Percent) { BreakBefore = discontinuity });
            previous = point;
        }
        return result;
    }

    private static double[] Values(IEnumerable<DailyModelTotals> rows, Dictionary<DateOnly, int> index, int days)
    {
        var values = new double[days];
        foreach (var row in rows)
        {
            if (index.TryGetValue(row.Day, out var i))
            {
                values[i] += row.Tokens.Total;
            }
        }

        return values;
    }

    private static string Describe(IReadOnlyList<TimePoint> points)
    {
        if (points.Count == 0)
        {
            return "no readings";
        }

        var peak = points.Max(p => p.Value);
        var now = Percent(points[^1].Value);
        return peak > points[^1].Value ? $"{now} now, peak {Percent(peak)}" : now + " now";
    }

    private static string DescribeRange(TimeSpan span) =>
        span.TotalDays >= 2
            ? Math.Round(span.TotalDays).ToString("0", CultureInfo.CurrentCulture) + " days"
            : Math.Round(span.TotalHours).ToString("0", CultureInfo.CurrentCulture) + " hours";

    private static string BurnSummary(BlockAnalytics block, IReadOnlyList<TimePoint> recorded, DateTimeOffset now, TimeZoneInfo zone)
    {
        var head = $"This Current window block, {Clock(block.Start, zone)} to {Clock(block.End, zone)}: ";
        if (recorded.Count == 0)
        {
            return head + "no readings yet.";
        }

        var body = $"{Percent(recorded[0].Value)} at {Clock(recorded[0].At, zone)}, {Percent(recorded[^1].Value)} now";
        var tail = block.ProjectedLimitAt switch
        {
            { } at when at > now && block.LimitBeforeReset => ". At this pace the limit lands at " + Clock(at, zone) + ", before the reset",
            { } at when at > now => ". At this pace the window resets before the limit",
            _ => string.Empty,
        };
        return head + body + tail + ".";
    }

    private static string DailySummary(double[] totals, List<string> labels, int days)
    {
        var sum = totals.Sum();
        if (sum <= 0)
        {
            return $"No tokens recorded in the last {days} days.";
        }

        var busiest = 0;
        for (var i = 1; i < totals.Length; i++)
        {
            if (totals[i] > totals[busiest])
            {
                busiest = i;
            }
        }

        return $"{TokenFormat.Compact(sum)} tokens in the last {days} days; busiest {labels[busiest]} with {TokenFormat.Compact(totals[busiest])}; today {TokenFormat.Compact(totals[^1])}.";
    }

    private static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("t", CultureInfo.CurrentCulture);

    private static string Percent(double value) => Math.Round(value).ToString("0", CultureInfo.CurrentCulture) + "%";
}
