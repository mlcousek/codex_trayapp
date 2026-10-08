using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Providers;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class PollingStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(300);

    private static UsageSnapshot Snapshot(double percent = 10) =>
        UsageSnapshot.Empty(Now) with { Windows = [UsageWindow.Create("codex:primary", percent, Now.AddHours(2))] };

    private static PollingStateMachine Machine(PollingOptions? options = null) => new(options ?? new PollingOptions());

    [Fact]
    public void The_interval_floor_cannot_be_configured_away()
    {
        new PollingOptions { Interval = TimeSpan.FromSeconds(10) }.EffectiveInterval.ShouldBe(PollingOptions.MinimumInterval);
        new PollingOptions { Interval = TimeSpan.FromSeconds(600) }.EffectiveInterval.ShouldBe(TimeSpan.FromSeconds(600));
        new PollingOptions().EffectiveInterval.ShouldBe(Interval);
    }

    [Fact]
    public void Success_moves_to_ok_and_schedules_the_next_interval()
    {
        var machine = Machine();
        var fetching = PollingStateMachine.BeginFetch(PollStatus.Initial(null), Now);

        var (status, delay) = machine.Complete(fetching, UsageFetchResult.Success(Snapshot()), Now.AddSeconds(1));

        status.State.ShouldBe(PollState.Ok);
        status.Snapshot.ShouldNotBeNull();
        status.LastSuccess.ShouldBe(Now.AddSeconds(1));
        status.NextAttempt.ShouldBe(Now.AddSeconds(1) + Interval);
        status.ConsecutiveFailures.ShouldBe(0);
        status.IsStale.ShouldBeFalse();
        status.HasPercentages.ShouldBeTrue();
        delay.ShouldBe(Interval);
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(2, 600)]
    [InlineData(3, 1200)]
    [InlineData(4, 1800)]
    [InlineData(10, 1800)]
    [InlineData(40, 1800)]
    public void Backoff_doubles_from_the_interval_and_caps_at_thirty_minutes(int failures, int expectedSeconds) =>
        Machine().Backoff(failures).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void Rate_limits_back_off_exponentially_and_keep_the_last_snapshot()
    {
        var machine = Machine();
        var status = machine.Complete(PollStatus.Initial(null), UsageFetchResult.Success(Snapshot()), Now).Status;
        var delays = new List<TimeSpan>();

        for (var i = 0; i < 5; i++)
        {
            TimeSpan delay;
            (status, delay) = machine.Complete(status, UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "429"), Now);
            delays.Add(delay);
        }

        delays.ShouldBe([TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(1200), TimeSpan.FromSeconds(1800), TimeSpan.FromSeconds(1800)]);
        status.State.ShouldBe(PollState.RateLimited);
        status.Snapshot.ShouldNotBeNull();
        status.IsStale.ShouldBeTrue();
        status.ConsecutiveFailures.ShouldBe(5);
        status.Message.ShouldBe("429");
    }

    [Fact]
    public void Retry_after_wins_when_it_is_longer_than_the_backoff()
    {
        var (status, delay) = Machine().Complete(
            PollStatus.Initial(null),
            UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "429", TimeSpan.FromMinutes(20)),
            Now);

        delay.ShouldBe(TimeSpan.FromMinutes(20));
        status.NextAttempt.ShouldBe(Now.AddMinutes(20));
    }

    [Fact]
    public void Retry_after_is_capped_at_the_maximum_backoff()
    {
        var (status, delay) = Machine().Complete(
            PollStatus.Initial(null),
            UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "429", TimeSpan.FromHours(6)),
            Now);

        delay.ShouldBe(PollingOptions.DefaultMaxBackoff);
        status.NextAttempt.ShouldBe(Now + PollingOptions.DefaultMaxBackoff);
    }

    [Fact]
    public void Retry_after_never_shortens_the_backoff()
    {
        var (_, delay) = Machine().Complete(
            PollStatus.Initial(null),
            UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "429", TimeSpan.FromSeconds(5)),
            Now);

        delay.ShouldBe(Interval);
    }

    [Theory]
    [InlineData(UsageFetchStatus.NetworkError)]
    [InlineData(UsageFetchStatus.ServerError)]
    [InlineData(UsageFetchStatus.ParseError)]
    public void Transient_failures_go_stale_with_backoff(UsageFetchStatus failure)
    {
        var machine = Machine();
        var ok = machine.Complete(PollStatus.Initial(null), UsageFetchResult.Success(Snapshot(42)), Now).Status;

        var (first, firstDelay) = machine.Complete(ok, UsageFetchResult.Failed(failure, "down"), Now);
        var (second, secondDelay) = machine.Complete(first, UsageFetchResult.Failed(failure, "down"), Now);

        first.State.ShouldBe(PollState.Stale);
        first.Snapshot.ShouldNotBeNull().Windows[0].UtilizationPercent.ShouldBe(42);
        firstDelay.ShouldBe(Interval);
        secondDelay.ShouldBe(Interval * 2);
        second.ConsecutiveFailures.ShouldBe(2);
    }

    [Theory]
    [InlineData(UsageFetchStatus.NoCredentials)]
    [InlineData(UsageFetchStatus.TokenExpired)]
    [InlineData(UsageFetchStatus.Unauthenticated)]
    public void Authentication_problems_wait_one_interval_without_backoff(UsageFetchStatus failure)
    {
        var (status, delay) = Machine().Complete(PollStatus.Initial(Snapshot()), UsageFetchResult.Failed(failure, "sign in"), Now);

        status.State.ShouldBe(PollState.Unauthenticated);
        status.Message.ShouldBe("sign in");
        status.Snapshot.ShouldBeNull();
        status.LastSuccess.ShouldBeNull();
        status.ConsecutiveFailures.ShouldBe(0);
        delay.ShouldBe(Interval);
    }

    [Fact]
    public void Success_resets_the_failure_counter()
    {
        var machine = Machine();
        var failed = machine.Complete(PollStatus.Initial(null), UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "x"), Now).Status;
        failed = machine.Complete(failed, UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "x"), Now).Status;

        var (ok, delay) = machine.Complete(failed, UsageFetchResult.Success(Snapshot()), Now);

        ok.ConsecutiveFailures.ShouldBe(0);
        delay.ShouldBe(Interval);
        machine.Complete(ok, UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "x"), Now).Delay.ShouldBe(Interval);
    }

    [Fact]
    public void Manual_refresh_is_allowed_when_idle()
    {
        Machine().CanRefreshNow(PollStatus.Initial(null), Now, out var reason).ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact]
    public void Manual_refresh_is_refused_while_fetching()
    {
        var fetching = PollingStateMachine.BeginFetch(PollStatus.Initial(null), Now);

        Machine().CanRefreshNow(fetching, Now, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("already running");
    }

    [Fact]
    public void Manual_refresh_cannot_bypass_a_rate_limit_backoff()
    {
        var machine = Machine();
        var limited = machine.Complete(PollStatus.Initial(null), UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "429"), Now).Status;

        machine.CanRefreshNow(limited, Now.AddMinutes(2), out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("Rate limited");
        machine.CanRefreshNow(limited, Now.AddMinutes(6), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(UsageFetchStatus.NetworkError)]
    [InlineData(UsageFetchStatus.ServerError)]
    [InlineData(UsageFetchStatus.ParseError)]
    [InlineData(UsageFetchStatus.SchemaChanged)]
    public void Manual_refresh_cannot_bypass_transient_failure_backoff(UsageFetchStatus failure)
    {
        var machine = Machine();
        var failed = machine.Complete(PollStatus.Initial(Snapshot()), UsageFetchResult.Failed(failure, "unavailable"), Now).Status;
        failed = machine.Complete(failed, UsageFetchResult.Failed(failure, "unavailable"), Now.AddMinutes(5)).Status;

        machine.CanRefreshNow(failed, Now.AddMinutes(7), out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("backoff");
        machine.CanRefreshNow(failed, Now.AddMinutes(15), out _).ShouldBeTrue();
    }

    [Fact]
    public void Manual_refresh_is_debounced_after_any_attempt()
    {
        var machine = Machine();
        var ok = machine.Complete(PollStatus.Initial(null), UsageFetchResult.Success(Snapshot()), Now).Status;

        machine.CanRefreshNow(ok, Now.AddSeconds(30), out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("wait");
        machine.CanRefreshNow(ok, Now.AddSeconds(61), out _).ShouldBeTrue();
    }

    [Fact]
    public void Initial_status_from_cache_is_stale_but_shows_percentages()
    {
        var status = PollStatus.Initial(Snapshot() with { Source = UsageSource.Cache });

        status.State.ShouldBe(PollState.Idle);
        status.IsStale.ShouldBeTrue();
        status.HasPercentages.ShouldBeTrue();
        status.Message.ShouldNotBeNull().ShouldContain("cached");
    }

    [Fact]
    public void Initial_status_without_cache_has_no_percentages()
    {
        var status = PollStatus.Initial(null);

        status.HasPercentages.ShouldBeFalse();
        status.IsStale.ShouldBeFalse();
    }
}
