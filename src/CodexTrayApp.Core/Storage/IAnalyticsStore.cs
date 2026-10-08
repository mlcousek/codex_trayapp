using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Core.Storage;

/// <summary>Usage events and scan offsets. Implementations are safe to call from any thread.</summary>
public interface IAnalyticsStore
{
    ScanState? GetScanState(string path);

    void SetScanState(ScanState state);

    /// <summary>Atomically clears derived history and replaces ingestion cursors with source baselines.</summary>
    void ClearAndBaseline(IReadOnlyCollection<ScanState> states, DateTimeOffset clearedAt);

    /// <summary>Historical records in new or rotated source files must not undo a user clear.</summary>
    DateTimeOffset? GetHistoryClearedAt();

    /// <summary>Inserts events, ignoring any whose (message id, request id) pair is already stored. Returns the number inserted.</summary>
    int InsertEvents(IReadOnlyCollection<UsageEvent> events);

    long CountEvents();

    IReadOnlyList<ModelTotals> TotalsByModel(DateTimeOffset since, DateTimeOffset until);

    IReadOnlyList<ProjectUsage> TopProjects(DateTimeOffset since, DateTimeOffset until, int count);

    /// <summary>Totals per local day and model. <paramref name="localOffset"/> shifts UTC timestamps into the user's day boundaries.</summary>
    IReadOnlyList<DailyModelTotals> DailyTotals(DateTimeOffset since, DateTimeOffset until, TimeSpan localOffset);
}

/// <summary>Time series of every successful snapshot, one row per window, plus retention.</summary>
public interface IHistoryStore
{
    void AppendSnapshot(UsageSnapshot snapshot);

    IReadOnlyList<HistoryPoint> GetSeries(string windowKey, DateTimeOffset since, DateTimeOffset until);

    /// <summary>Reads a fixed account partition, independent of a mutable active account.</summary>
    IReadOnlyList<HistoryPoint> GetSeries(string windowKey, DateTimeOffset since, DateTimeOffset until, string? accountKey) =>
        GetSeries(windowKey, since, until);

    IReadOnlyList<string> GetWindowKeys();

    /// <summary>Lists windows in a fixed account partition.</summary>
    IReadOnlyList<string> GetWindowKeys(string? accountKey) => GetWindowKeys();

    /// <summary>Deletes history rows and usage events older than <paramref name="before"/>. Returns rows removed.</summary>
    int Prune(DateTimeOffset before);

    /// <summary>Removes all history rows and usage events, keeping nothing but the schema.</summary>
    void Clear();
}
