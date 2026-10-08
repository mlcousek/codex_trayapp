using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Storage;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class AnalyticsProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(1);

    private static UsageSnapshot Snapshot(double percent = 60, DateTimeOffset? at = null,
        long? duration = 300, DateTimeOffset? reset = null, UsageSource source = UsageSource.Live,
        string key = WindowKeys.FiveHour) => new(
            [UsageWindow.Create(key, percent, reset ?? Reset) with { DurationMinutes = duration }],
            "test", null, null, at ?? Now, source);

    private static HistoryPoint Point(int minutesAgo, double percent, DateTimeOffset? reset = null) =>
        new(Now.AddMinutes(-minutesAgo), percent, reset ?? Reset);

    private static BlockAnalytics Compute(UsageSnapshot snapshot, params HistoryPoint[] samples)
    {
        var store = Substitute.For<IAnalyticsStore, IHistoryStore>();
        store.TotalsByModel(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns([new ModelTotals("test", new TokenTotals(400, 0, 0, 0, 0, 1))]);
        ((IHistoryStore)store).GetSeries(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>())
            .Returns(info => samples.Where(p => p.Timestamp >= info.ArgAt<DateTimeOffset>(1)
                && p.Timestamp < info.ArgAt<DateTimeOffset>(2)).ToArray());
        return new AnalyticsCalculator(store, () => PricingTable.Empty, TimeZoneInfo.Utc)
            .Compute(snapshot, Now).CurrentBlock!;
    }

    private static void Suppressed(BlockAnalytics block)
    {
        block.PercentPerHour.ShouldBeNull();
        block.ProjectedLimitAt.ShouldBeNull();
        block.LimitBeforeReset.ShouldBeFalse();
        block.TokensPerHour.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Current_authoritative_sample_completes_three_sample_projection()
    {
        var block = Compute(Snapshot(), Point(10, 40), Point(5, 50));
        block.PercentPerHour.ShouldBe(120);
        block.ProjectedLimitAt.ShouldBe(Now.AddMinutes(20));
        block.LimitBeforeReset.ShouldBeTrue();
        block.Tokens.Total.ShouldBe(400);
        block.TokensPerHour.ShouldBe(100);
    }

    [Fact]
    public void Projection_is_anchored_to_observation_time_not_calculation_time()
    {
        var block = Compute(Snapshot(at: Now.AddMinutes(-5)), Point(15, 40), Point(10, 50));
        block.PercentPerHour.ShouldBe(120);
        block.ProjectedLimitAt.ShouldBe(Now.AddMinutes(15));
    }

    [Fact]
    public void Sparse_samples_do_not_manufacture_quota_from_local_tokens() =>
        Suppressed(Compute(Snapshot(), Point(10, 40)));

    [Fact]
    public void Three_samples_must_span_ten_minutes() =>
        Suppressed(Compute(Snapshot(), Point(9, 40), Point(5, 50)));

    [Theory]
    [InlineData(-11)]
    [InlineData(1)]
    public void Stale_or_future_live_snapshot_suppresses_estimate(int offsetMinutes) =>
        Suppressed(Compute(Snapshot(at: Now.AddMinutes(offsetMinutes)), Point(30, 30), Point(20, 40), Point(15, 50)));

    [Fact]
    public void Cached_snapshot_suppresses_estimate() =>
        Suppressed(Compute(Snapshot(source: UsageSource.Cache), Point(10, 40), Point(5, 50)));

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void Missing_or_invalid_duration_suppresses_estimate(long? duration) =>
        Suppressed(Compute(Snapshot(duration: duration), Point(10, 40), Point(5, 50)));

    [Fact]
    public void Missing_reset_suppresses_estimate()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Windows = [snapshot.PrimaryWindow! with { ResetsAt = null }] };
        Suppressed(Compute(snapshot, Point(10, 40), Point(5, 50)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void Expired_or_inconsistent_reset_suppresses_estimate(int minutesUntilReset) =>
        Suppressed(Compute(Snapshot(reset: Now.AddMinutes(minutesUntilReset)), Point(10, 40), Point(5, 50)));

    [Fact]
    public void Any_percent_drop_within_period_suppresses_estimate() =>
        Suppressed(Compute(Snapshot(), Point(15, 20), Point(10, 50), Point(5, 40)));

    [Fact]
    public void Drop_in_current_snapshot_suppresses_old_increasing_estimate() =>
        Suppressed(Compute(Snapshot(30), Point(15, 20), Point(10, 40), Point(5, 50)));

    [Fact]
    public void Zero_slope_suppresses_estimate() =>
        Suppressed(Compute(Snapshot(), Point(10, 60), Point(5, 60)));

    [Fact]
    public void Unrepresentably_slow_pace_suppresses_estimate() =>
        Suppressed(Compute(Snapshot(1e-20), Point(10, 0), Point(5, 0)));

    [Fact]
    public void Persisted_current_snapshot_does_not_count_twice() =>
        Suppressed(Compute(Snapshot(), Point(10, 40), Point(0, 60)));

    [Fact]
    public void Slow_positive_projection_after_reset_does_not_claim_limit_before_reset()
    {
        var block = Compute(Snapshot(41), Point(10, 40), Point(5, 40.5));
        block.PercentPerHour.ShouldBe(6);
        block.ProjectedLimitAt.ShouldBe(Now.AddHours(59.0 / 6));
        block.LimitBeforeReset.ShouldBeFalse();
    }

    [Fact]
    public void Changed_reset_waits_for_three_samples_in_new_period() =>
        Suppressed(Compute(Snapshot(), Point(20, 10, Reset.AddHours(-1)), Point(10, 40, Reset.AddHours(-1)), Point(5, 50)));

    [Fact]
    public void Intervening_reset_identity_cannot_be_filtered_out_to_reuse_old_period() =>
        Suppressed(Compute(Snapshot(), Point(20, 10), Point(15, 20), Point(10, 30, Reset.AddHours(1)), Point(5, 50)));

    [Fact]
    public void Duplicate_timestamps_are_not_three_distinct_samples() =>
        Suppressed(Compute(Snapshot(), Point(10, 40), Point(10, 40)));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Invalid_history_percentage_suppresses_estimate(double percent) =>
        Suppressed(Compute(Snapshot(), Point(10, percent), Point(5, 50)));

    [Fact]
    public void Later_history_cannot_complete_older_snapshot() =>
        Suppressed(Compute(Snapshot(at: Now.AddMinutes(-5)), Point(10, 40), Point(2, 70)));

    [Fact]
    public void Changed_bucket_does_not_use_old_bucket_history()
    {
        var store = Substitute.For<IAnalyticsStore, IHistoryStore>();
        var history = (IHistoryStore)store;
        history.GetSeries(WindowKeys.FiveHour, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>())
            .Returns([Point(10, 40), Point(5, 50)]);
        var block = new AnalyticsCalculator(store, () => PricingTable.Empty).Compute(Snapshot(key: "new:primary"), Now).CurrentBlock!;
        block.PercentPerHour.ShouldBeNull();
        block.ProjectedLimitAt.ShouldBeNull();
        history.Received(1).GetSeries("new:primary", Arg.Any<DateTimeOffset>(), Now, null);
        history.DidNotReceive().GetSeries(WindowKeys.FiveHour, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>());
    }
}
