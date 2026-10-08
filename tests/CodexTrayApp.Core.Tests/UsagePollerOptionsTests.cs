using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Tests.TestSupport;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class UsagePollerOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot() =>
        UsageSnapshot.Empty(Now) with { Windows = [UsageWindow.Create("codex:primary", 20, Now.AddHours(2))] };

    [Fact]
    public void A_healthy_loop_is_retimed_from_its_last_attempt()
    {
        var status = new PollStatus(PollState.Ok, Snapshot(), Now, Now, Now.AddSeconds(300), null, 0);
        var planned = Now.AddSeconds(300);

        UsagePoller.DeadlineAfterOptionsChange(status, new PollingOptions { Interval = TimeSpan.FromSeconds(180) }, planned)
            .ShouldBe(Now.AddSeconds(180));
        UsagePoller.DeadlineAfterOptionsChange(status, new PollingOptions { Interval = TimeSpan.FromSeconds(900) }, planned)
            .ShouldBe(Now.AddSeconds(900));
        UsagePoller.DeadlineAfterOptionsChange(status, new PollingOptions { Interval = TimeSpan.FromSeconds(10) }, planned)
            .ShouldBe(Now + PollingOptions.MinimumInterval);
    }

    [Fact]
    public void A_backoff_keeps_its_plan()
    {
        var status = new PollStatus(PollState.RateLimited, Snapshot(), Now.AddMinutes(-10), Now, Now.AddMinutes(20), "429", 2);
        var planned = Now.AddMinutes(20);

        UsagePoller.DeadlineAfterOptionsChange(status, new PollingOptions { Interval = TimeSpan.FromSeconds(180) }, planned).ShouldBe(planned);
    }

    [Fact]
    public async Task Shortening_the_interval_wakes_the_loop()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Name.Returns("fake provider");
        var fetches = 0;
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref fetches);
            return UsageFetchResult.Success(Snapshot());
        });
        var clock = new FakeTimeProvider(Now);
        using var poller = new UsagePoller(provider, new InMemorySnapshotCache(), new PollingOptions(), clock, new ListLogger<UsagePoller>());
        using var cancellation = new CancellationTokenSource();

        var loop = poller.RunAsync(cancellation.Token);
        await WaitUntil(() => Volatile.Read(ref fetches) == 1);

        // The loop is now waiting 300 s. Move the clock past a 180 s interval and shorten it: the wait must end at once.
        clock.Advance(TimeSpan.FromSeconds(200));
        poller.UpdateOptions(new PollingOptions { Interval = TimeSpan.FromSeconds(180) });
        await WaitUntil(() => Volatile.Read(ref fetches) == 2);

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => loop);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
