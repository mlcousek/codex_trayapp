using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexTrayApp.Charts;

/// <summary>
/// Runs the history and daily queries for one chart refresh. Meant for a thread-pool thread: the store opens a
/// connection per call, and nothing here touches WPF.
/// </summary>
public sealed class ChartDataLoader
{
    private readonly IHistoryStore _history;
    private readonly AnalyticsCalculator _calculator;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _logger;

    public ChartDataLoader(IHistoryStore history, AnalyticsCalculator calculator, TimeZoneInfo? zone = null, ILogger? logger = null)
    {
        _history = history;
        _calculator = calculator;
        _zone = zone ?? TimeZoneInfo.Local;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Loads every chart. Codename windows whose history is flat at zero are left out of the history chart unless
    /// <paramref name="showInactiveWindows"/> asks for them, matching what the flyout lists.
    /// Each chart loads on its own: a query that fails (a damaged database, say) leaves that one chart empty and is
    /// logged, while the rest still load. Before, one failing query froze every chart on whatever it last showed.
    /// </summary>
    public ChartBundle Load(UsageSnapshot? snapshot, BlockAnalytics? block, int rangeHours, DateTimeOffset now, bool showInactiveWindows = false)
    {
        var accountKey = snapshot?.AccountKey;
        var from = now - TimeSpan.FromHours(Math.Max(1, rangeHours));
        var history = Section(
            "history",
            () =>
            {
                var windows = OrderKeys(_history.GetWindowKeys(accountKey))
                    .Select(key => (key, snapshot?.FindWindow(key)?.DisplayName ?? WindowNameHumanizer.Humanize(key), _history.GetSeries(key, from, now, accountKey)))
                    .Where(w => showInactiveWindows || IsKnown(w.key) || w.Item3.Any(p => p.Percent > 0))
                    .ToList();
                return ChartDataBuilder.History(windows, from, now);
            },
            () => ChartDataBuilder.History([], from, now));

        var burn = block is null
            ? null
            : Section<BurnChart?>(
                "5-hour block",
                () => ChartDataBuilder.Burn(_history.GetSeries(snapshot?.PrimaryWindow?.Key ?? WindowKeys.FiveHour, block.Start, now, accountKey), block, snapshot?.PrimaryWindow?.UtilizationPercent, now, _zone),
                () => null);

        var localNow = TimeZoneInfo.ConvertTime(now, _zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var firstDay = new DateTimeOffset(localNow.Date, localNow.Offset).AddDays(-(ChartDataBuilder.DailyDays - 1));
        var daily = Section(
            "daily",
            () => ChartDataBuilder.Daily(_calculator.Daily(firstDay, now), today),
            () => ChartDataBuilder.Daily([], today));

        var sparklines = new Dictionary<string, SparkData>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in snapshot?.Windows ?? [])
        {
            var (since, until) = SparkRange(window.Key, block, now);
            var points = burn is not null && string.Equals(window.Key, WindowKeys.FiveHour, StringComparison.OrdinalIgnoreCase)
                ? burn.Recorded
                : Section<IReadOnlyList<TimePoint>>(
                    "sparkline",
                    () => ChartDataBuilder.Sparkline(_history.GetSeries(window.Key, since, until, accountKey), since, until),
                    () => []);
            sparklines[window.Key] = new SparkData(since, until, points);
        }

        return new ChartBundle(history, burn, daily, sparklines);
    }

    /// <summary>Known windows in their preferred order, then unknown keys alphabetically.</summary>
    internal static IReadOnlyList<string> OrderKeys(IEnumerable<string> keys) =>
        keys.OrderBy(Rank).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>The 5-hour sparkline spans its block; weekly windows span a week; anything else the last day.</summary>
    internal static (DateTimeOffset Since, DateTimeOffset Until) SparkRange(string key, BlockAnalytics? block, DateTimeOffset now)
    {
        if (string.Equals(key, WindowKeys.FiveHour, StringComparison.OrdinalIgnoreCase))
        {
            return block is not null ? (block.Start, block.End) : (now - AnalyticsCalculator.BlockLength, now);
        }

        return key.StartsWith("seven_day", StringComparison.OrdinalIgnoreCase)
            ? (now - TimeSpan.FromDays(7), now)
            : (now - TimeSpan.FromHours(24), now);
    }

    private static bool IsKnown(string key) => Rank(key) != int.MaxValue;

    private static int Rank(string key)
    {
        for (var i = 0; i < WindowKeys.PreferredOrder.Count; i++)
        {
            if (string.Equals(WindowKeys.PreferredOrder[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private T Section<T>(string chart, Func<T> load, Func<T> empty)
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The {Chart} chart could not be loaded; the other charts are unaffected", chart);
            return empty();
        }
    }
}
