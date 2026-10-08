using CodexTrayApp.Core.Cache;
using CodexTrayApp.Core.Diagnostics;
using CodexTrayApp.Core.Providers;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Polling;

/// <summary>
/// Drives the polling loop: cache on start, fetch, apply the state machine, persist, wait.
/// Manual refreshes go through the same limiter as the timer and can never bypass a backoff.
/// </summary>
public sealed class UsagePoller : IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly ISnapshotCache _cache;
    private readonly PollingStateMachine _machine;
    private readonly TimeProvider _clock;
    private readonly ILogger<UsagePoller> _logger;
    private readonly SemaphoreSlim _refreshSignal = new(0);
    private readonly SemaphoreSlim _optionsSignal = new(0);
    private readonly SemaphoreSlim _resumeSignal = new(0);
    private readonly object _sync = new();
    private PollStatus _status = PollStatus.Initial(null);
    private bool _initialised;
    private int _invalidateCache;

    public UsagePoller(
        IUsageProvider provider,
        ISnapshotCache cache,
        PollingOptions options,
        TimeProvider clock,
        ILogger<UsagePoller> logger)
    {
        _provider = provider;
        _cache = cache;
        _machine = new PollingStateMachine(options);
        _clock = clock;
        _logger = logger;
        if (provider is IAccountChangeSource accounts) { accounts.AccountChanged += OnAccountChanged; }
    }

    public event EventHandler<PollStatus>? StatusChanged;

    public PollStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>Loads the cached snapshot so the UI has something to show before the first fetch completes.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return;
        }

        _initialised = true;
        var cached = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            Publish(PollStatus.Initial(cached));
            _logger.LogInformation("Loaded cached snapshot from {When}", cached.LastUpdated);
        }
    }

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await InitialiseAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = await FetchOnceAsync(cancellationToken).ConfigureAwait(false);
            await WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One fetch cycle. Returns the delay the state machine wants before the next attempt.</summary>
    public async Task<TimeSpan> FetchOnceAsync(CancellationToken cancellationToken)
    {
        Publish(PollingStateMachine.BeginFetch(Status, _clock.GetUtcNow()));

        UsageFetchResult result;
        try
        {
            result = await _provider.FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DeactivateCacheIfNeededAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Usage provider {Provider} threw", _provider.Name);
            result = UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "Unexpected error while fetching usage.");
        }

        var completedAt = _clock.GetUtcNow();
        var (next, delay) = _machine.Complete(Status, result, completedAt);

        await DeactivateCacheIfNeededAsync().ConfigureAwait(false);

        if (result.IsSuccess && result.Snapshot is { } snapshot)
        {
            await _cache.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Usage updated: {Summary}", UsageSnapshotFormatter.Describe(snapshot, completedAt));
        }
        else
        {
            _logger.LogWarning("Usage fetch {Status}: {Message} Next attempt in {Delay}", result.Status, result.Message, delay);
        }

        Publish(next);
        return delay;
    }

    /// <summary>Asks for a refresh now. Returns false, with a user-facing reason, when the limiter says no.</summary>
    public bool TryRequestRefresh(out string? reason)
    {
        if (!_machine.CanRefreshNow(Status, _clock.GetUtcNow(), out reason))
        {
            return false;
        }

        _refreshSignal.Release();
        return true;
    }

    /// <summary>
    /// Replaces the polling options (the interval floor is enforced by the options themselves). A wait in progress is
    /// re-timed from the last attempt, so a shorter interval takes effect without waiting out the old one.
    /// </summary>
    public void UpdateOptions(PollingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _machine.Options = options;
        _optionsSignal.Release();
    }

    /// <summary>After an options change: a healthy loop re-times from its last attempt; a backoff keeps its plan.</summary>
    internal static DateTimeOffset DeadlineAfterOptionsChange(PollStatus status, PollingOptions options, DateTimeOffset planned) =>
        status.State == PollState.Ok && status.LastAttempt is { } last ? last + options.EffectiveInterval : planned;

    /// <summary>Wake the loop after Windows resumes; coalesce signals and preserve retry limits.</summary>
    public void RequestResumeRefresh() => _resumeSignal.Release();

    internal static DateTimeOffset DeadlineAfterResume(PollStatus status, PollingOptions options, DateTimeOffset now)
    {
        var earliest = status.LastAttempt is { } last ? last + options.ManualRefreshFloor : now;
        if (status.ConsecutiveFailures > 0 && status.NextAttempt is { } retry && retry > earliest)
        {
            earliest = retry;
        }

        return earliest > now ? earliest : now;
    }

    public void Dispose()
    {
        if (_provider is IAccountChangeSource accounts) { accounts.AccountChanged -= OnAccountChanged; }
        _refreshSignal.Dispose();
        _optionsSignal.Dispose();
        _resumeSignal.Dispose();
    }

    private static void Drain(SemaphoreSlim signal)
    {
        while (signal.CurrentCount > 0 && signal.Wait(0))
        {
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + delay;
        while (true)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timer = Task.Delay(remaining, _clock, waitCancellation.Token);
            var refresh = _refreshSignal.WaitAsync(waitCancellation.Token);
            var options = _optionsSignal.WaitAsync(waitCancellation.Token);
            var resume = _resumeSignal.WaitAsync(waitCancellation.Token);

            var finished = await Task.WhenAny(timer, refresh, options, resume).ConfigureAwait(false);
            if (finished == resume)
            {
                await resume.ConfigureAwait(false);
                await waitCancellation.CancelAsync().ConfigureAwait(false);
                Drain(_resumeSignal);
                deadline = DeadlineAfterResume(Status, _machine.Options, _clock.GetUtcNow());
                _logger.LogDebug("Windows resumed; next attempt at {Deadline}", deadline);
                continue;
            }
            if (finished == refresh)
            {
                await refresh.ConfigureAwait(false);
                await waitCancellation.CancelAsync().ConfigureAwait(false);
                Drain(_refreshSignal);
                if (_machine.CanRefreshNow(Status, _clock.GetUtcNow(), out _))
                {
                    _logger.LogDebug("Manual refresh requested");
                    return;
                }

                continue;
            }

            if (finished == options)
            {
                await options.ConfigureAwait(false);
                await waitCancellation.CancelAsync().ConfigureAwait(false);
                Drain(_optionsSignal);
                deadline = DeadlineAfterOptionsChange(Status, _machine.Options, deadline);
                _logger.LogDebug("Polling options changed; next attempt at {Deadline}", deadline);
                continue;
            }

            // The timer won: drop the pending waits so a later release is not consumed by a stale waiter.
            await waitCancellation.CancelAsync().ConfigureAwait(false);
            await timer.ConfigureAwait(false);
            return;
        }
    }

    private void Publish(PollStatus status)
    {
        lock (_sync)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private void OnAccountChanged(object? sender, AccountIdentityChangedEventArgs e)
    {
        var current = Status;
        if (current.Snapshot is { } snapshot
            && (e.SourceKey is { } sourceKey ? snapshot.AccountSourceKey == sourceKey
                : e.AccountKey is { } accountKey ? snapshot.AccountKey == accountKey
                : e.IdentityKey is { } identityKey && snapshot.AccountIdentityKey == identityKey))
        {
            return;
        }

        Interlocked.Exchange(ref _invalidateCache, 1);
        Publish(current with { Snapshot = null, LastSuccess = null });
    }

    private async Task DeactivateCacheIfNeededAsync()
    {
        if (Interlocked.Exchange(ref _invalidateCache, 0) == 0) { return; }
        // The caller may be shutting down during a new account's quota read. Do not reuse its cancelled token.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _cache.DeactivateAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _invalidateCache, 1);
            _logger.LogWarning("Account cache deactivation timed out");
        }
    }
}
