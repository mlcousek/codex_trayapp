using CodexTrayApp.Charts;
using CodexTrayApp.Core.Analytics;
using Shouldly;

namespace CodexTrayApp.Tests;

public class ChartDataBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BlockStart = new(2026, 9, 7, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BlockEnd = new(2026, 9, 7, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void History_breaks_at_missing_intervals_reset_changes_and_decreasing_usage()
    {
        var reset = Now.AddHours(2);
        var points = new HistoryPoint[]
        {
            new(Now, 10, reset), new(Now.AddMinutes(5), 20, reset),
            new(Now.AddMinutes(25), 30, reset), new(Now.AddMinutes(30), 5, reset),
            new(Now.AddMinutes(35), 10, reset.AddHours(5)),
        };
        var history = ChartDataBuilder.History([("window", "Window", points)], Now, Now.AddHours(1));
        history.Series[0].Points.Select(p => p.BreakBefore).ShouldBe([false, false, true, true, true]);
        ChartDataBuilder.Sparkline(points, Now, Now.AddHours(1)).Select(p => p.BreakBefore)
            .ShouldBe([false, false, true, true, true]);
    }

    [Fact]
    public void Downsampling_preserves_the_low_value_on_both_sides_of_a_gap()
    {
        var points = Enumerable.Range(0, 1000).Select(i => new TimePoint(Now.AddSeconds(i), i is 499 or 500 ? 1 : 99)
        { BreakBefore = i == 500 }).ToList();
        var sampled = ChartDataBuilder.Downsample(points, Now, Now.AddSeconds(1000), 20);
        sampled.ShouldContain(points[499]);
        sampled.ShouldContain(points[500]);
        var gap = sampled.ToList().FindIndex(p => p.BreakBefore);
        sampled[gap - 1].ShouldBe(points[499]);
    }

    [Fact]
    public void Downsample_keeps_peaks_and_both_ends()
    {
        var from = Now.AddHours(-24);
        var points = Enumerable.Range(0, 1000)
            .Select(i => new TimePoint(from.AddSeconds(i * 86.4), i == 500 ? 99 : 20))
            .ToList();

        var result = ChartDataBuilder.Downsample(points, from, Now, 100);

        result.Count.ShouldBeLessThanOrEqualTo(102);
        result[0].ShouldBe(points[0]);
        result[^1].ShouldBe(points[^1]);
        result.ShouldContain(p => p.Value == 99);
        result.Zip(result.Skip(1)).ShouldAllBe(pair => pair.First.At < pair.Second.At);
    }

    [Fact]
    public void Downsample_leaves_short_series_alone()
    {
        var points = new List<TimePoint> { new(Now.AddHours(-1), 10), new(Now, 20) };

        ChartDataBuilder.Downsample(points, Now.AddHours(-24), Now, 240).ShouldBeSameAs(points);
    }

    [Fact]
    public void History_names_every_window_and_summarises_it()
    {
        var from = Now.AddHours(-24);
        var chart = ChartDataBuilder.History(
            [
                ("codex:primary", "Current window", [Point(from, 10), Point(Now.AddHours(-2), 60), Point(Now, 40)]),
                ("codex:secondary", "Secondary window", [Point(from, 8), Point(Now, 12)]),
                ("empty", "Empty", []),
            ],
            from,
            Now);

        chart.HasData.ShouldBeTrue();
        chart.Series.Select(s => s.Key).ShouldBe(["codex:primary", "codex:secondary"]);
        chart.Summary.ShouldBe("Last 24 hours: Current window 40% now, peak 60%; Secondary window 12% now.");
    }

    [Fact]
    public void History_without_rows_is_honest()
    {
        var chart = ChartDataBuilder.History([], Now.AddDays(-7), Now);

        chart.HasData.ShouldBeFalse();
        chart.Summary.ShouldStartWith("No history recorded yet");
    }

    [Fact]
    public void Burn_projects_to_the_limit_inside_the_block()
    {
        var block = Block(percentPerHour: 30, projectedLimitAt: Now.AddHours(2));

        var chart = ChartDataBuilder.Burn([Point(BlockStart, 0), Point(Now.AddHours(-1), 20)], block, 40, Now, TimeZoneInfo.Utc);

        chart.Recorded[^1].ShouldBe(new TimePoint(Now, 40) { BreakBefore = true });
        chart.Projection.ShouldBe([new TimePoint(Now, 40), new TimePoint(Now.AddHours(2), 100)]);
        chart.LimitAt.ShouldBe(Now.AddHours(2));
        chart.LimitBeforeReset.ShouldBeTrue();
        chart.Summary.ShouldContain("before the reset");
        chart.HasData.ShouldBeTrue();
    }

    [Fact]
    public void Burn_stops_the_projection_at_the_reset_when_the_limit_lands_later()
    {
        var block = Block(percentPerHour: 10, projectedLimitAt: Now.AddHours(6), limitBeforeReset: false);

        var chart = ChartDataBuilder.Burn([Point(BlockStart, 0)], block, 40, Now, TimeZoneInfo.Utc);

        chart.Projection.ShouldBe([new TimePoint(Now, 40), new TimePoint(BlockEnd, 70)]);
        chart.LimitAt.ShouldBeNull();
        chart.Summary.ShouldContain("resets before the limit");
    }

    [Fact]
    public void Burn_without_a_pace_has_no_projection()
    {
        var block = Block(percentPerHour: null, projectedLimitAt: null);

        var chart = ChartDataBuilder.Burn([Point(BlockStart.AddHours(-1), 90), Point(BlockStart, 5)], block, 12, Now, TimeZoneInfo.Utc);

        chart.Recorded.Select(p => p.Value).ShouldBe([5, 12]);
        chart.Projection.ShouldBeEmpty();
        chart.Summary.ShouldBe($"This Current window block, {Clock(BlockStart)} to {Clock(BlockEnd)}: 5% at {Clock(BlockStart)}, 12% now.");
    }

    [Fact]
    public void Daily_ends_today_and_groups_the_long_tail()
    {
        var today = new DateOnly(2026, 9, 7);
        var rows = new List<DailyModelTotals>();
        for (var m = 0; m < 6; m++)
        {
            rows.Add(new DailyModelTotals(today, $"codex-model-{m}", Tokens(1000 * (6 - m))));
            rows.Add(new DailyModelTotals(today.AddDays(-1), $"codex-model-{m}", Tokens(100)));
        }

        rows.Add(new DailyModelTotals(today.AddDays(-20), "codex-model-0", Tokens(999_999)));

        var chart = ChartDataBuilder.Daily(rows, today);

        chart.Days.Count.ShouldBe(14);
        chart.Days[^1].ShouldBe(today);
        chart.Labels[^1].ShouldBe("Today");
        chart.ByModel.Select(s => s.Model).ShouldBe(["codex-model-0", "codex-model-1", "codex-model-2", "codex-model-3", "other"]);
        chart.ByModel[^1].Values[^1].ShouldBe(2000 + 1000);
        chart.Totals[^1].ShouldBe(21000);
        chart.Totals[^2].ShouldBe(600);
        chart.Totals.Take(12).ShouldAllBe(t => t == 0);
        chart.HasData.ShouldBeTrue();
        chart.Summary.ShouldBe("22k tokens in the last 14 days; busiest Today with 21k; today 21k.");
    }

    [Fact]
    public void Daily_without_rows_is_honest()
    {
        var chart = ChartDataBuilder.Daily([], new DateOnly(2026, 9, 7));

        chart.HasData.ShouldBeFalse();
        chart.ByModel.ShouldBeEmpty();
        chart.Summary.ShouldBe("No tokens recorded in the last 14 days.");
    }

    [Fact]
    public void Sparkline_keeps_only_points_in_range()
    {
        var points = new[] { Point(Now.AddDays(-2), 50), Point(Now.AddHours(-3), 10), Point(Now, 30) };

        var spark = ChartDataBuilder.Sparkline(points, Now.AddHours(-24), Now);

        spark.Select(p => p.Value).ShouldBe([10, 30]);
    }

    [Theory]
    [InlineData("codex-sonnet-4-5-20250929", "sonnet-4-5")]
    [InlineData("codex-fable-5-1", "fable-5-1")]
    [InlineData("codex-opus-4-1-20250805", "opus-4-1")]
    [InlineData("<synthetic>", "<synthetic>")]
    [InlineData("gpt-x", "gpt-x")]
    [InlineData("codex-", "codex-")]
    public void Short_model_names_drop_the_vendor_prefix_and_the_date(string model, string expected) =>
        ChartDataBuilder.ShortModelName(model).ShouldBe(expected);

    private static HistoryPoint Point(DateTimeOffset at, double percent) => new(at, percent, null);

    private static TokenTotals Tokens(long input) => new(input, 0, 0, 0, 0, 1);

    private static string Clock(DateTimeOffset at) => at.ToString("t", System.Globalization.CultureInfo.CurrentCulture);

    private static BlockAnalytics Block(double? percentPerHour, DateTimeOffset? projectedLimitAt, bool limitBeforeReset = true) =>
        new(BlockStart, BlockEnd, Now - BlockStart, TokenTotals.Zero, null, 0, percentPerHour, projectedLimitAt, limitBeforeReset && projectedLimitAt is not null);
}
