using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.History;

/// <summary>Retention in force. Mutable so a settings change applies without rebuilding the recorder.</summary>
public sealed class HistoryOptions
{
    public int RetentionDays { get; set; } = 90;
}

/// <summary>Appends every fresh snapshot to the history store and prunes rows past the retention window once a day.</summary>
public sealed class HistoryRecorder : IDisposable
{
    private readonly IHistoryStore _store;
    private readonly HistoryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<HistoryRecorder> _logger;
    private UsagePoller? _poller;
    private DateTimeOffset? _lastRecorded;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public HistoryRecorder(IHistoryStore store, HistoryOptions options, TimeProvider clock, ILogger<HistoryRecorder> logger)
    {
        _store = store;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public void Attach(UsagePoller poller)
    {
        ArgumentNullException.ThrowIfNull(poller);
        _poller = poller;
        poller.StatusChanged += OnStatusChanged;
    }

    /// <summary>Records a status if it carries a snapshot newer than the last recorded one. Returns true when a row was written.</summary>
    public bool Record(PollStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State != PollState.Ok || status.Snapshot is not { } snapshot || snapshot.LastUpdated == _lastRecorded)
        {
            return false;
        }

        try
        {
            if (_store is SqliteStore sqlite) { sqlite.ActiveAccountKey = snapshot.AccountKey; }
            _store.AppendSnapshot(snapshot);
            _lastRecorded = snapshot.LastUpdated;
            PruneIfDue();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "History could not be recorded");
            return false;
        }
    }

    public void Dispose()
    {
        _poller?.StatusChanged -= OnStatusChanged;
    }

    /// <summary>Prunes past the retention window right away, for example after the setting changed. Returns rows removed.</summary>
    public int PruneNow()
    {
        var now = _clock.GetUtcNow();
        _lastPrune = now;
        var removed = _store.Prune(now.AddDays(-Math.Max(1, _options.RetentionDays)));
        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Rows} history rows older than {Days} days", removed, _options.RetentionDays);
        }

        return removed;
    }

    private void PruneIfDue()
    {
        if (_clock.GetUtcNow() - _lastPrune >= TimeSpan.FromHours(24))
        {
            PruneNow();
        }
    }

    private void OnStatusChanged(object? sender, PollStatus status) => Record(status);
}
