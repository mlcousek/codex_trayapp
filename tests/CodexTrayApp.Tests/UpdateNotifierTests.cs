using CodexTrayApp.Core.Updates;
using CodexTrayApp.Startup;
using Shouldly;

namespace CodexTrayApp.Tests;

public class UpdateNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly ReleaseInfo Release = new(new Version(0, 2, 0), "v0.2.0", "https://example.invalid/v0.2.0");

    [Fact]
    public void Nothing_is_known_before_the_first_check()
    {
        var notifier = new UpdateNotifier();

        notifier.Last.ShouldBeNull();
        notifier.LastChecked.ShouldBeNull();
        notifier.Available.ShouldBeNull();
    }

    [Fact]
    public void An_available_release_is_offered_with_the_time_it_was_found()
    {
        var notifier = new UpdateNotifier();

        notifier.Report(UpdateCheckOutcome.Available(Release), Now);

        notifier.Available.ShouldBe(Release);
        notifier.LastChecked.ShouldBe(Now);
    }

    [Fact]
    public void Up_to_date_and_failed_checks_offer_no_release()
    {
        var notifier = new UpdateNotifier();

        notifier.Report(UpdateCheckOutcome.UpToDate(), Now);
        notifier.Available.ShouldBeNull();

        notifier.Report(UpdateCheckOutcome.Failed("offline"), Now.AddDays(7));
        notifier.Available.ShouldBeNull();
        notifier.Last.ShouldNotBeNull().Message.ShouldBe("offline");
        notifier.LastChecked.ShouldBe(Now.AddDays(7));
    }

    [Fact]
    public void A_later_failure_replaces_an_earlier_offer()
    {
        var notifier = new UpdateNotifier();
        notifier.Report(UpdateCheckOutcome.Available(Release), Now);

        notifier.Report(UpdateCheckOutcome.Failed("offline"), Now.AddDays(7));

        notifier.Available.ShouldBeNull();
    }

    [Fact]
    public void Every_report_raises_Changed()
    {
        var notifier = new UpdateNotifier();
        var raised = 0;
        notifier.Changed += (_, _) => raised++;

        notifier.Report(UpdateCheckOutcome.UpToDate(), Now);
        notifier.Report(UpdateCheckOutcome.UpToDate(), Now);

        raised.ShouldBe(2);
    }

    [Fact]
    public void A_missing_outcome_is_refused() =>
        Should.Throw<ArgumentNullException>(() => new UpdateNotifier().Report(null!, Now));
}
