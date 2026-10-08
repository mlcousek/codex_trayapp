using CodexTrayApp.Core.Aggregation;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class UsageAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(UsageSource source) =>
        UsageSnapshot.Empty(Now) with { Windows = [UsageWindow.Create("codex:primary", 42, Now.AddHours(2))], Source = source };

    private static LocalAnalytics Local() => new(
        Now,
        new PeriodAnalytics(Now, Now, TokenTotals.Zero, null, false, []),
        [],
        null,
        PricingTable.Empty,
        0);

    private static PollStatus Status(UsageSnapshot? snapshot) => PollStatus.Initial(snapshot) with { State = snapshot is null ? PollState.Idle : PollState.Ok };

    [Fact]
    public void A_live_snapshot_with_local_analytics_reports_both_available_and_the_live_source()
    {
        var aggregated = UsageAggregator.Combine(Status(Snapshot(UsageSource.Live)), Local(), Now);

        aggregated.PercentagesAvailable.ShouldBeTrue();
        aggregated.AnalyticsAvailable.ShouldBeTrue();
        aggregated.PercentagesSource.ShouldBe("usage endpoint");
        aggregated.Snapshot.ShouldNotBeNull();
    }

    [Fact]
    public void A_cached_snapshot_reports_the_cached_source_even_though_percentages_are_still_available()
    {
        var aggregated = UsageAggregator.Combine(Status(Snapshot(UsageSource.Cache)), null, Now);

        aggregated.PercentagesAvailable.ShouldBeTrue();
        aggregated.AnalyticsAvailable.ShouldBeFalse();
        aggregated.PercentagesSource.ShouldBe("usage endpoint, cached");
    }

    [Fact]
    public void No_snapshot_at_all_means_percentages_are_unavailable_regardless_of_local_analytics()
    {
        var withAnalytics = UsageAggregator.Combine(Status(null), Local(), Now);
        var withoutAnalytics = UsageAggregator.Combine(Status(null), null, Now);

        withAnalytics.PercentagesAvailable.ShouldBeFalse();
        withAnalytics.AnalyticsAvailable.ShouldBeTrue();
        withoutAnalytics.PercentagesAvailable.ShouldBeFalse();
        withoutAnalytics.AnalyticsAvailable.ShouldBeFalse();
        // With no snapshot at all the source label still defaults to "usage endpoint" (not e.g. "unavailable") -
        // PercentagesAvailable is what callers must check before trusting it.
        withoutAnalytics.PercentagesSource.ShouldBe("usage endpoint");
    }

    [Fact]
    public void A_snapshot_with_no_windows_has_no_percentages_even_though_it_is_a_live_snapshot()
    {
        var emptySnapshot = UsageSnapshot.Empty(Now);
        var aggregated = UsageAggregator.Combine(Status(emptySnapshot), null, Now);

        aggregated.Snapshot.ShouldNotBeNull();
        aggregated.PercentagesAvailable.ShouldBeFalse();
        aggregated.PercentagesSource.ShouldBe("usage endpoint");
    }

    [Fact]
    public void Combine_rejects_a_null_poll_status()
    {
        Should.Throw<ArgumentNullException>(() => UsageAggregator.Combine(null!, null, Now));
    }
}
