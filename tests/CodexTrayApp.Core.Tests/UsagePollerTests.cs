using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Tests.TestSupport;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class UsagePollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(double percent) =>
        UsageSnapshot.Empty(Now) with { Windows = [UsageWindow.Create("codex:primary", percent, Now.AddHours(2))] };

    private static (UsagePoller Poller, IUsageProvider Provider, InMemorySnapshotCache Cache, FakeTimeProvider Clock, List<PollStatus> Events) Create()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Name.Returns("fake provider");
        var cache = new InMemorySnapshotCache();
        var clock = new FakeTimeProvider(Now);
        var poller = new UsagePoller(provider, cache, new PollingOptions(), clock, new ListLogger<UsagePoller>());
        var events = new List<PollStatus>();
        poller.StatusChanged += (_, status) => events.Add(status);
        return (poller, provider, cache, clock, events);
    }

    [Fact]
    public async Task A_successful_fetch_is_cached_and_published()
    {
        var (poller, provider, cache, _, events) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Success(Snapshot(51)));

        var delay = await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        delay.ShouldBe(PollingOptions.DefaultInterval);
        poller.Status.State.ShouldBe(PollState.Ok);
        poller.Status.Snapshot.ShouldNotBeNull().Windows[0].UtilizationPercent.ShouldBe(51);
        cache.Saves.ShouldBe(1);
        events.Select(e => e.State).ShouldBe([PollState.Fetching, PollState.Ok]);
    }

    [Fact]
    public async Task Starts_from_the_cache_and_keeps_it_when_the_endpoint_fails()
    {
        var (poller, provider, cache, _, _) = Create();
        cache.Stored = Snapshot(33);
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "offline"));

        await poller.InitialiseAsync(TestContext.Current.CancellationToken);
        poller.Status.State.ShouldBe(PollState.Idle);
        poller.Status.IsStale.ShouldBeTrue();
        poller.Status.Snapshot.ShouldNotBeNull().Source.ShouldBe(UsageSource.Cache);

        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        poller.Status.State.ShouldBe(PollState.Stale);
        poller.Status.Snapshot.ShouldNotBeNull().Windows[0].UtilizationPercent.ShouldBe(33);
        poller.Status.Message.ShouldBe("offline");
        cache.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task A_throwing_provider_does_not_crash_the_loop()
    {
        var (poller, provider, _, _, _) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var delay = await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        poller.Status.State.ShouldBe(PollState.Stale);
        delay.ShouldBe(PollingOptions.DefaultInterval);
    }

    [Fact]
    public async Task Manual_refresh_is_debounced_after_an_attempt()
    {
        var (poller, provider, _, clock, _) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Success(Snapshot(1)));
        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        poller.TryRequestRefresh(out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("wait");

        clock.Advance(TimeSpan.FromSeconds(61));
        poller.TryRequestRefresh(out reason).ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact]
    public async Task The_loop_stops_promptly_when_cancelled_while_waiting()
    {
        var (poller, provider, _, _, _) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Success(Snapshot(1)));
        using var cancellation = new CancellationTokenSource();

        var loop = poller.RunAsync(cancellation.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        loop.IsCompleted.ShouldBeFalse();

        await cancellation.CancelAsync();
        var finished = await Task.WhenAny(loop, Task.Delay(2000, TestContext.Current.CancellationToken));

        finished.ShouldBe(loop);
        await Should.ThrowAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_a_fetch()
    {
        var (poller, provider, _, _, _) = Create();
        using var cancellation = new CancellationTokenSource();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await cancellation.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return UsageFetchResult.Success(Snapshot(1));
        });

        await Should.ThrowAsync<OperationCanceledException>(() => poller.FetchOnceAsync(cancellation.Token));
    }

    /// <summary>
    /// Regression for a leftover-signal bug: two TryRequestRefresh() calls in quick succession (a UI double-invoke,
    /// both issued while the poller is still idle and has not yet consumed either) used to leave the refresh
    /// semaphore's count at 1 after the first was drained, so the wait loop's *next* cycle treated that leftover
    /// count as a fresh manual refresh and fetched immediately - without ever calling CanRefreshNow again, silently
    /// bypassing ManualRefreshFloor. The fix drains the semaphore in the same branch that consumes it.
    /// </summary>
    [Fact]
    public async Task Two_rapid_manual_refreshes_never_cause_a_third_unrequested_fetch()
    {
        var (poller, provider, _, clock, _) = Create();
        var fetchCount = 0;
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult(UsageFetchResult.Success(Snapshot(1)));
        });

        using var cancellation = new CancellationTokenSource();
        var loop = poller.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => poller.Status.State == PollState.Ok, TestContext.Current.CancellationToken);
        fetchCount.ShouldBe(1);

        // Move past the manual-refresh floor so a real refresh is allowed, then fire two requests back to back:
        // both see the same (unchanged) status and so both pass the floor check, exactly like a double click.
        clock.Advance(PollingOptions.DefaultManualRefreshFloor + TimeSpan.FromSeconds(1));
        poller.TryRequestRefresh(out var reason1).ShouldBeTrue();
        reason1.ShouldBeNull();
        poller.TryRequestRefresh(out var reason2).ShouldBeTrue();
        reason2.ShouldBeNull();

        // The one legitimate manual refresh they represent should complete (this can race straight past a
        // buggy leftover fetch too, so wait for "at least" rather than "exactly" before settling below).
        await WaitUntilAsync(() => fetchCount >= 2, TestContext.Current.CancellationToken);

        // ...but the leftover signal from the second call must not trigger a further, unrequested fetch.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        fetchCount.ShouldBe(2);

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => loop);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (!condition())
        {
            linked.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, linked.Token);
        }
    }

    [Fact]
    public async Task Resume_wakes_a_healthy_loop_without_waiting_for_the_poll_interval()
    {
        var (poller, provider, _, clock, _) = Create();
        using var ownedPoller = poller;
        var count = 0;
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(UsageFetchResult.Success(Snapshot(1)));
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = poller.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(() => poller.Status.State == PollState.Ok, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(61));
            poller.RequestResumeRefresh();
            await WaitUntilAsync(() => Volatile.Read(ref count) == 2, TestContext.Current.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Resume_cannot_bypass_failure_backoff_or_the_refresh_floor()
    {
        var (poller, provider, _, clock, _) = Create();
        using var ownedPoller = poller;
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "limited", TimeSpan.FromMinutes(20)));
        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        UsagePoller.DeadlineAfterResume(poller.Status, new PollingOptions(), clock.GetUtcNow()).ShouldBe(Now.AddMinutes(20));
        clock.Advance(TimeSpan.FromMinutes(25));
        UsagePoller.DeadlineAfterResume(poller.Status, new PollingOptions(), clock.GetUtcNow()).ShouldBe(clock.GetUtcNow());

        var healthy = poller.Status with { State = PollState.Ok, ConsecutiveFailures = 0, LastAttempt = clock.GetUtcNow() };
        UsagePoller.DeadlineAfterResume(healthy, new PollingOptions(), clock.GetUtcNow()).ShouldBe(clock.GetUtcNow().AddSeconds(60));
    }

    [Fact]
    public async Task Refresh_queued_before_a_scheduled_failure_cannot_bypass_its_new_backoff()
    {
        var (poller, provider, _, _, _) = Create();
        using (poller)
        using (var cancellation = new CancellationTokenSource())
        {
            var count = 0;
            provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref count);
                return Task.FromResult(UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "offline"));
            });
            poller.TryRequestRefresh(out _).ShouldBeTrue();
            var loop = poller.RunAsync(cancellation.Token);
            await WaitUntilAsync(() => poller.Status.State == PollState.Stale, TestContext.Current.CancellationToken);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            count.ShouldBe(1);
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => loop);
        }
    }

    [Fact]
    public async Task Verified_account_change_clears_old_usage_before_waiting_for_new_limits()
    {
        var provider = new SwitchingProvider();
        var cache = new InMemorySnapshotCache();
        using var poller = new UsagePoller(provider, cache, new PollingOptions(), TimeProvider.System, new ListLogger<UsagePoller>());
        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);
        poller.Status.Snapshot.ShouldNotBeNull().AccountKey.ShouldBe("synthetic-a");
        provider.Switch = true;
        var fetch = poller.FetchOnceAsync(TestContext.Current.CancellationToken);
        await provider.AccountRead.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        poller.Status.Snapshot.ShouldBeNull();
        poller.Status.LastSuccess.ShouldBeNull();
        provider.Limits.SetResult(UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "new account offline"));
        await fetch;
        poller.Status.Snapshot.ShouldBeNull();
        cache.Stored.ShouldBeNull();
        cache.Deactivations.ShouldBe(1);
    }

    private sealed class SwitchingProvider : IUsageProvider, IAccountChangeSource
    {
        public event EventHandler<AccountIdentityChangedEventArgs>? AccountChanged;
        public string Name => "Synthetic account switch";
        public bool Switch { get; set; }
        public TaskCompletionSource AccountRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UsageFetchResult> Limits { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            if (!Switch) { return Task.FromResult(UsageFetchResult.Success(Snapshot(21) with { AccountKey = "synthetic-a" })); }
            AccountChanged?.Invoke(this, new AccountIdentityChangedEventArgs(null));
            AccountRead.TrySetResult();
            return Limits.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Cancellation_during_new_account_limits_deactivates_old_persisted_cache()
    {
        var provider = new SwitchingProvider();
        var cache = new InMemorySnapshotCache();
        using var poller = new UsagePoller(provider, cache, new PollingOptions(), TimeProvider.System, new ListLogger<UsagePoller>());
        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);
        provider.Switch = true;
        using var cancellation = new CancellationTokenSource();
        var fetch = poller.FetchOnceAsync(cancellation.Token);
        await provider.AccountRead.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => fetch);
        cache.Stored.ShouldBeNull();
        cache.Deactivations.ShouldBe(1);
    }
}
