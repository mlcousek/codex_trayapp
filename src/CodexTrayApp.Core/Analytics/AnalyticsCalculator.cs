using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Storage;

namespace CodexTrayApp.Core.Analytics;

/// <summary>
/// Derives today's totals, top projects and the current quota block from the store. Block timing comes from the
/// endpoint's reset time when known; the projection uses the endpoint's percentage, never a locally invented one.
/// </summary>
public sealed class AnalyticsCalculator
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);
    private static readonly TimeSpan MaximumSampleAge = TimeSpan.FromMinutes(10);
    private readonly IAnalyticsStore _store;
    private readonly Func<PricingTable> _pricing;
    private readonly TimeZoneInfo _zone;

    public AnalyticsCalculator(IAnalyticsStore store, Func<PricingTable> pricing, TimeZoneInfo? zone = null)
    {
        _store = store;
        _pricing = pricing;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public LocalAnalytics Compute(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var pricing = _pricing();
        var localNow = TimeZoneInfo.ConvertTime(now, _zone);
        var dayStart = LocalMidnightUtc(localNow.Date);

        var today = Period(dayStart, now, pricing);
        var projects = _store.TopProjects(dayStart, now, 3);
        var block = ComputeBlock(snapshot, now, pricing);
        return new LocalAnalytics(now, today, projects, block, pricing, _store.CountEvents());
    }

    /// <summary>
    /// Totals per local day, used by the daily chart. Each day's UTC boundaries are resolved
    /// independently so daylight-saving transitions use their actual 23- or 25-hour day.
    /// </summary>
    public IReadOnlyList<DailyModelTotals> Daily(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = new List<DailyModelTotals>();
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, _zone).DateTime);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var start = LocalMidnightUtc(day.ToDateTime(TimeOnly.MinValue));
            var end = LocalMidnightUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
            rows.AddRange(_store.TotalsByModel(start > from ? start : from, end < to ? end : to).Select(m => new DailyModelTotals(day, m.Model, m.Tokens)));
        }
        return rows;
    }

    /// <summary>UTC instant of local midnight on <paramref name="localDate"/>, resolved in <see cref="_zone"/> so a
    /// DST transition between midnight and "now" cannot shift the day boundary (unlike reusing now's offset).</summary>
    private DateTimeOffset LocalMidnightUtc(DateTime localDate)
    {
        var midnightUnspecified = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnightUnspecified, _zone), TimeSpan.Zero);
    }

    internal PeriodAnalytics Period(DateTimeOffset from, DateTimeOffset to, PricingTable pricing)
    {
        var byModel = _store.TotalsByModel(from, to)
            .Select(t => new ModelUsage(t.Model, t.Tokens, pricing.Cost(t.Model, t.Tokens)))
            .ToList();

        var tokens = byModel.Aggregate(TokenTotals.Zero, (sum, m) => sum.Add(m.Tokens));
        var known = byModel.Where(m => m.Cost is not null).ToList();
        var unknownWithTokens = byModel.Any(m => m.Cost is null && m.Tokens.Total > 0);
        decimal? cost = known.Count > 0 ? known.Sum(m => m.Cost!.Value) : null;
        return new PeriodAnalytics(from, to, tokens, cost, unknownWithTokens, byModel);
    }

    internal BlockAnalytics? ComputeBlock(UsageSnapshot? snapshot, DateTimeOffset now, PricingTable pricing)
    {
        var window = snapshot?.PrimaryWindow;
        var durationKnown = window?.DurationMinutes is > 0
            && window.DurationMinutes <= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMinute;
        var length = durationKnown ? TimeSpan.FromMinutes(window!.DurationMinutes!.Value) : BlockLength;
        // Guard malformed durations that cannot be represented as a date range.
        if (length > now - DateTimeOffset.MinValue) { durationKnown = false; length = BlockLength; }
        var resetKnown = window?.ResetsAt is { } resets && resets > now && resets - now <= length;
        var end = resetKnown ? window!.ResetsAt!.Value : now;
        var start = end - length;
        var period = Period(start, now, pricing);
        var elapsed = now - start;
        if (period.Tokens.Total == 0 && !resetKnown)
        {
            return null;
        }

        var hours = Math.Max(elapsed.TotalHours, 1.0 / 60);
        var tokensPerHour = period.Tokens.Total / hours;

        double? percentPerHour = null;
        DateTimeOffset? projected = null;
        if (resetKnown && durationKnown && snapshot?.Source == UsageSource.Live
            && snapshot.LastUpdated <= now && now - snapshot.LastUpdated <= MaximumSampleAge
            && snapshot.LastUpdated >= start && _store is IHistoryStore history)
        {
            // The store's upper bound is exclusive. Include the authoritative current snapshot
            // explicitly, and never use later history to estimate an older snapshot.
            var samples = history.GetSeries(window!.Key, start, snapshot.LastUpdated, snapshot.AccountKey)
                .Where(p => p.Timestamp >= start && p.Timestamp < snapshot.LastUpdated)
                .Append(new HistoryPoint(snapshot.LastUpdated, window.UtilizationPercent, window.ResetsAt))
                .OrderByDescending(p => p.Timestamp)
                .TakeWhile(p => p.ResetsAt == window.ResetsAt)
                .OrderBy(p => p.Timestamp).ToList();
            var distinctTimes = samples.Select(p => p.Timestamp).Distinct().Count() == samples.Count;
            if (samples.Count >= 3 && samples[^1].Timestamp - samples[0].Timestamp >= TimeSpan.FromMinutes(10)
                && distinctTimes && samples.All(p => double.IsFinite(p.Percent) && p.Percent is >= 0 and <= 100)
                && samples.Zip(samples.Skip(1), (a, b) => b.Percent >= a.Percent).All(x => x))
            {
                var slope = (samples[^1].Percent - samples[0].Percent) / (samples[^1].Timestamp - samples[0].Timestamp).TotalHours;
                var hoursUntilLimit = (100 - window.UtilizationPercent) / slope;
                if (double.IsFinite(slope) && slope > 0 && double.IsFinite(hoursUntilLimit)
                    && hoursUntilLimit <= (DateTimeOffset.MaxValue - snapshot.LastUpdated).TotalHours)
                {
                    var untilLimit = TimeSpan.FromHours(hoursUntilLimit);
                    if (untilLimit <= DateTimeOffset.MaxValue - snapshot.LastUpdated)
                    {
                        percentPerHour = slope;
                        projected = snapshot.LastUpdated + untilLimit;
                    }
                }
            }
        }

        return new BlockAnalytics(
            start,
            end,
            elapsed,
            period.Tokens,
            period.Cost,
            tokensPerHour,
            percentPerHour,
            projected,
            projected is { } p && p < end);
    }
}
