using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Notifications;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class ThresholdNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly int[] Thresholds = [80, 95];

    private static UsageSnapshot Snapshot(double fiveHour, DateTimeOffset? resetsAt, double sevenDay = 10) =>
        UsageSnapshot.Empty(Now) with
        {
            AccountKey = "synthetic-account",
            AccountIdentityKey = "synthetic-identity",
            AccountSourceKey = "synthetic-source",
            Windows = [UsageWindow.Create("codex:primary", fiveHour, resetsAt), UsageWindow.Create("codex:secondary", sevenDay, Now.AddDays(3))],
        };

    [Fact]
    public void Reports_the_highest_threshold_crossed_once_per_period()
    {
        var notifier = new ThresholdNotifier();
        var reset = Now.AddHours(2);

        var first = notifier.Evaluate(Snapshot(97, reset), Thresholds, Now);
        var again = notifier.Evaluate(Snapshot(98, reset), Thresholds, Now);

        first.Count.ShouldBe(1);
        first[0].WindowKey.ShouldBe("codex:primary");
        first[0].WindowName.ShouldBe("Current window");
        first[0].Threshold.ShouldBe(95);
        first[0].Percent.ShouldBe(97);
        first[0].ResetsAt.ShouldBe(reset);
        again.ShouldBeEmpty();
    }

    [Fact]
    public void Each_threshold_fires_as_it_is_crossed()
    {
        var notifier = new ThresholdNotifier();
        var reset = Now.AddHours(2);

        notifier.Evaluate(Snapshot(50, reset), Thresholds, Now).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(81, reset), Thresholds, Now).Single().Threshold.ShouldBe(80);
        notifier.Evaluate(Snapshot(90, reset), Thresholds, Now).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(96, reset), Thresholds, Now).Single().Threshold.ShouldBe(95);
        notifier.Evaluate(Snapshot(99, reset), Thresholds, Now).ShouldBeEmpty();
    }

    [Fact]
    public void A_new_period_alerts_again()
    {
        var notifier = new ThresholdNotifier();

        notifier.Evaluate(Snapshot(85, Now.AddHours(2)), Thresholds, Now).Count.ShouldBe(1);
        notifier.Evaluate(Snapshot(85, Now.AddHours(2)), Thresholds, Now).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(85, Now.AddHours(7)), Thresholds, Now).Count.ShouldBe(1);
    }

    [Fact]
    public void Windows_alert_independently()
    {
        var notifier = new ThresholdNotifier();

        var alerts = notifier.Evaluate(Snapshot(85, Now.AddHours(2), sevenDay: 96), Thresholds, Now);

        alerts.Select(a => (a.WindowKey, a.Threshold)).ShouldBe([("codex:primary", 80), ("codex:secondary", 95)]);
    }

    [Fact]
    public void No_thresholds_means_no_alerts_and_reset_forgets()
    {
        var notifier = new ThresholdNotifier();

        notifier.Evaluate(Snapshot(99, Now.AddHours(2)), [], Now).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(99, Now.AddHours(2)), Thresholds, Now).Count.ShouldBe(1);
        notifier.Reset();
        notifier.Evaluate(Snapshot(99, Now.AddHours(2)), Thresholds, Now).Count.ShouldBe(1);
    }

    [Fact]
    public void Unverified_cached_old_future_and_unknown_period_samples_do_not_consume_crossings()
    {
        var notifier = new ThresholdNotifier();
        var valid = Snapshot(99, Now.AddHours(2));
        UsageSnapshot[] suppressed =
        [
            valid with { Source = UsageSource.Cache },
            valid with { LastUpdated = Now.AddMinutes(-11) },
            valid with { LastUpdated = Now.AddSeconds(1) },
            valid with { AccountKey = null },
            valid with { AccountIdentityKey = null },
            valid with { AccountSourceKey = null },
            Snapshot(99, null),
            Snapshot(99, Now),
            Snapshot(99, Now.AddSeconds(-1)),
        ];
        foreach (var snapshot in suppressed)
        {
            notifier.Evaluate(snapshot, Thresholds, Now).ShouldBeEmpty();
        }

        notifier.Evaluate(valid, Thresholds, Now).Single().Threshold.ShouldBe(95);
    }

    [Fact]
    public void Account_identity_and_source_partitions_are_independent()
    {
        var notifier = new ThresholdNotifier();
        var snapshot = Snapshot(85, Now.AddHours(2));
        notifier.Evaluate(snapshot, Thresholds, Now).Count.ShouldBe(1);
        notifier.Evaluate(snapshot with { AccountKey = "synthetic-other" }, Thresholds, Now).Count.ShouldBe(1);
        notifier.Evaluate(snapshot with { AccountIdentityKey = "synthetic-other" }, Thresholds, Now).Count.ShouldBe(1);
        notifier.Evaluate(snapshot with { AccountSourceKey = "synthetic-other" }, Thresholds, Now).Count.ShouldBe(1);
        notifier.Evaluate(snapshot, Thresholds, Now).ShouldBeEmpty();
    }

    [Fact]
    public void Enabling_a_lower_threshold_after_a_higher_alert_does_not_emit_a_lower_alert()
    {
        var notifier = new ThresholdNotifier();
        var snapshot = Snapshot(99, Now.AddHours(2));
        notifier.Evaluate(snapshot, [95], Now).Single().Threshold.ShouldBe(95);
        notifier.Evaluate(snapshot, Thresholds, Now).ShouldBeEmpty();
    }
}
