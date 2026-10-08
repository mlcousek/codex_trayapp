using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.ViewModels;
using Shouldly;

namespace CodexTrayApp.Tests;

public class ConnectionStatusTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UsageFetchStatus.CliMissing, "Codex CLI not found")]
    [InlineData(UsageFetchStatus.ApiKeyUnsupported, "API-key sign-in")]
    [InlineData(UsageFetchStatus.CliIncompatible, "Codex CLI update required")]
    [InlineData(UsageFetchStatus.NetworkError, "Connection unavailable")]
    public void Failures_are_distinguishable_without_cached_data(UsageFetchStatus failure, string label)
    {
        var status = new PollStatus(PollState.Stale, null, null, Now, Now.AddMinutes(5), "Recovery guidance.", 1) { Failure = failure };
        var banner = ConnectionStatusText.Banner(status, Now).ShouldNotBeNull();
        banner.ShouldStartWith(label + ".");
        banner.ShouldContain("Recovery guidance.");
        banner.ShouldContain("Next retry in 5m.");
        banner.ShouldNotContain("last known values");
    }

    [Fact]
    public void Refresh_preserves_cached_warning_but_hides_the_previous_error_and_retry()
    {
        var status = new PollStatus(PollState.Fetching, UsageSnapshot.Empty(Now), Now, Now, Now.AddMinutes(5), "Old error", 1)
        { Failure = UsageFetchStatus.NetworkError };
        var banner = ConnectionStatusText.Banner(status, Now).ShouldNotBeNull();
        banner.ShouldStartWith("Refreshing.");
        banner.ShouldContain("last known values");
        banner.ShouldNotContain("Old error");
        banner.ShouldNotContain("retry");
        ConnectionStatusText.Banner(status with { State = PollState.Ok, Failure = null }, Now).ShouldBeNull();
    }
}
