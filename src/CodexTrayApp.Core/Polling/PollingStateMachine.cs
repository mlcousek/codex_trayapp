using System.Globalization;
using CodexTrayApp.Core.Diagnostics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Providers;

namespace CodexTrayApp.Core.Polling;

/// <summary>Polling cadence. The interval floor is a hard rule and cannot be configured away.</summary>
public sealed record PollingOptions
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultManualRefreshFloor = TimeSpan.FromSeconds(60);

    public TimeSpan Interval { get; init; } = DefaultInterval;

    public TimeSpan MaxBackoff { get; init; } = DefaultMaxBackoff;

    /// <summary>Minimum gap between attempts when the user asks for a refresh by hand.</summary>
    public TimeSpan ManualRefreshFloor { get; init; } = DefaultManualRefreshFloor;

    /// <summary>The interval actually used: never below the floor, whatever the settings say.</summary>
    public TimeSpan EffectiveInterval => Interval < MinimumInterval ? MinimumInterval : Interval;
}

public enum PollState
{
    Idle,
    Fetching,
    Ok,
    Stale,
    RateLimited,
    Unauthenticated,
}

/// <summary>What the UI needs to know about the polling loop. <see cref="Message"/> is user-facing.</summary>
public sealed record PollStatus(
    PollState State,
    UsageSnapshot? Snapshot,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastAttempt,
    DateTimeOffset? NextAttempt,
    string? Message,
    int ConsecutiveFailures)
{
    public UsageFetchStatus? Failure { get; init; }

    public static PollStatus Initial(UsageSnapshot? cached) => new(
        PollState.Idle,
        cached,
        cached?.LastUpdated,
        null,
        null,
        cached is null ? null : "Showing cached data until the first refresh completes.",
        0);

    /// <summary>True when a snapshot is shown but it is not fresh from the endpoint.</summary>
    public bool IsStale => Snapshot is not null && State != PollState.Ok;

    /// <summary>True when percentages can be shown at all. When false the UI must say "percentages unavailable", never invent them.</summary>
    public bool HasPercentages => Snapshot is { Windows.Count: > 0 };
}

/// <summary>
/// Pure transition logic for the polling loop: given the current status and a fetch result, produce the next
/// status and the delay before the next attempt. No timers, no I/O, fully unit-testable.
/// </summary>
public sealed class PollingStateMachine
{
    // Written from the settings thread, read by the polling loop.
    private volatile PollingOptions _options;

    public PollingStateMachine(PollingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Options in force; replaced when the user changes the poll interval.</summary>
    public PollingOptions Options
    {
        get => _options;
        set => _options = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static PollStatus BeginFetch(PollStatus current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with { State = PollState.Fetching, LastAttempt = now };
    }

    public (PollStatus Status, TimeSpan Delay) Complete(PollStatus current, UsageFetchResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);

        var interval = Options.EffectiveInterval;
        current = current with { Failure = result.IsSuccess ? null : result.Status };
        switch (result.Status)
        {
            case UsageFetchStatus.Success when result.Snapshot is not null:
                return (current with
                {
                    State = PollState.Ok,
                    Snapshot = result.Snapshot,
                    LastSuccess = now,
                    LastAttempt = now,
                    NextAttempt = now + interval,
                    Message = null,
                    ConsecutiveFailures = 0,
                }, interval);

            case UsageFetchStatus.RateLimited:
            {
                var failures = current.ConsecutiveFailures + 1;
                // Retry-After is honoured, but never beyond the backoff cap: a far-future header must not stall polling for hours.
                var retryAfter = Min(result.RetryAfter ?? TimeSpan.Zero, Options.MaxBackoff);
                var delay = Max(Backoff(failures), retryAfter);
                return (current with
                {
                    State = PollState.RateLimited,
                    LastAttempt = now,
                    NextAttempt = now + delay,
                    Message = result.Message ?? "Rate limited by the usage endpoint.",
                    ConsecutiveFailures = failures,
                }, delay);
            }

            case UsageFetchStatus.NoCredentials:
            case UsageFetchStatus.TokenExpired:
            case UsageFetchStatus.Unauthenticated:
            case UsageFetchStatus.CliMissing:
            case UsageFetchStatus.ApiKeyUnsupported:
                return (current with
                {
                    State = PollState.Unauthenticated,
                    Snapshot = null,
                    LastSuccess = null,
                    LastAttempt = now,
                    NextAttempt = now + interval,
                    Message = result.Message ?? "Not signed in. Run the Codex CLI to sign in.",
                    ConsecutiveFailures = 0,
                }, interval);

            default:
            {
                var failures = current.ConsecutiveFailures + 1;
                var delay = Backoff(failures);
                return (current with
                {
                    State = PollState.Stale,
                    LastAttempt = now,
                    NextAttempt = now + delay,
                    Message = result.Message ?? "The usage endpoint is unavailable.",
                    ConsecutiveFailures = failures,
                }, delay);
            }
        }
    }

    /// <summary>Exponential backoff starting at the effective interval and doubling per failure, capped at <see cref="PollingOptions.MaxBackoff"/>.</summary>
    public TimeSpan Backoff(int consecutiveFailures)
    {
        var interval = Options.EffectiveInterval;
        var exponent = Math.Clamp(consecutiveFailures, 1, 16) - 1;
        var factor = 1L << exponent;
        var candidate = interval.Ticks > long.MaxValue / factor ? Options.MaxBackoff : TimeSpan.FromTicks(interval.Ticks * factor);
        return Max(interval, Min(candidate, Options.MaxBackoff));
    }

    /// <summary>Whether a manual refresh may run now. A backoff can never be bypassed by hand.</summary>
    public bool CanRefreshNow(PollStatus current, DateTimeOffset now, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.State == PollState.Fetching)
        {
            reason = "A refresh is already running.";
            return false;
        }

        if (current.ConsecutiveFailures > 0 && current.NextAttempt is { } next && next > now)
        {
            reason = (current.State == PollState.RateLimited ? "Rate limited" : "Retry backoff")
                + "; next attempt in " + UsageSnapshotFormatter.FormatDuration(next - now) + ".";
            return false;
        }

        if (current.LastAttempt is { } last && now - last < Options.ManualRefreshFloor)
        {
            var wait = Math.Ceiling((Options.ManualRefreshFloor - (now - last)).TotalSeconds);
            reason = string.Create(CultureInfo.InvariantCulture, $"Please wait {wait:0} s between refreshes.");
            return false;
        }

        reason = null;
        return true;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
}
