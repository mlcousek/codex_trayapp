using CodexTrayApp.Core.Diagnostics;
using CodexTrayApp.Core.Domain;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class UsageSnapshotFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_empty_snapshot_describes_itself_as_having_no_usage_windows()
    {
        var snapshot = UsageSnapshot.Empty(Now);

        UsageSnapshotFormatter.Describe(snapshot, Now).ShouldStartWith("no usage windows | plan: unknown");
    }

    [Fact]
    public void A_missing_plan_tier_falls_back_to_unknown()
    {
        var snapshot = UsageSnapshot.Empty(Now) with { PlanTier = null };

        UsageSnapshotFormatter.Describe(snapshot, Now).ShouldContain("plan: unknown");
    }

    [Fact]
    public void An_unrecognised_plan_tier_is_shown_as_is_not_replaced()
    {
        var snapshot = UsageSnapshot.Empty(Now) with { PlanTier = "some-future-tier" };

        UsageSnapshotFormatter.Describe(snapshot, Now).ShouldContain("plan: some-future-tier");
    }

    [Fact]
    public void An_enabled_overage_adds_an_extra_usage_line()
    {
        var snapshot = UsageSnapshot.Empty(Now) with
        {
            Overage = new OverageInfo(true, 12.5m, 20m, 62.5, "USD"),
        };

        UsageSnapshotFormatter.Describe(snapshot, Now).ShouldContain("extra usage 12.5/20 USD");
    }

    [Fact]
    public void A_disabled_overage_adds_no_extra_usage_line()
    {
        var snapshot = UsageSnapshot.Empty(Now) with
        {
            Overage = new OverageInfo(false, 12.5m, 20m, 62.5, "USD"),
        };

        UsageSnapshotFormatter.Describe(snapshot, Now).ShouldNotContain("extra usage");
    }

    [Fact]
    public void A_locked_window_reports_its_reason_alongside_the_reset_countdown()
    {
        var window = UsageWindow.Create("codex:primary", 90, Now.AddHours(1), lockedReason: "Manually locked by support");

        UsageSnapshotFormatter.DescribeWindow(window, Now).ShouldBe("Current window 90% (resets in 1h 0m, locked: Manually locked by support)");
    }

    [Fact]
    public void An_unlocked_window_with_no_reset_time_says_so()
    {
        var window = UsageWindow.Create("codex:primary", 10, null);

        UsageSnapshotFormatter.DescribeWindow(window, Now).ShouldBe("Current window 10% (no reset time)");
    }

    [Fact]
    public void A_window_whose_reset_has_already_passed_says_reset_due()
    {
        var window = UsageWindow.Create("codex:primary", 100, Now.AddMinutes(-5));

        UsageSnapshotFormatter.DescribeWindow(window, Now).ShouldBe("Current window 100% (reset due)");
    }

    [Theory]
    [InlineData(0, 0, 0, "1m")]
    [InlineData(0, 0, 30, "1m")]
    [InlineData(0, 12, 0, "12m")]
    [InlineData(1, 26, 0, "1h 26m")]
    [InlineData(24, 0, 0, "1d 0h")]
    [InlineData(27, 3, 0, "1d 3h")]
    public void FormatDuration_never_shows_seconds_and_switches_units_at_the_hour_and_day_marks(int hours, int minutes, int seconds, string expected)
    {
        var duration = new TimeSpan(hours, minutes, seconds);

        UsageSnapshotFormatter.FormatDuration(duration).ShouldBe(expected);
    }

    [Fact]
    public void A_negative_duration_is_clamped_rather_than_shown_as_is()
    {
        UsageSnapshotFormatter.FormatDuration(TimeSpan.FromMinutes(-90)).ShouldBe("1m");
    }

    [Fact]
    public void FormatDuration_at_exactly_zero_shows_one_minute_never_zero_minutes()
    {
        UsageSnapshotFormatter.FormatDuration(TimeSpan.Zero).ShouldBe("1m");
    }
}
