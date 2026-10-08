using CodexTrayApp.Core.Pricing;

namespace CodexTrayApp.Core.Analytics;

/// <summary>One API response as recorded in a Codex CLI session log. Deduplicated by message id and request id.</summary>
public sealed record UsageEvent(
    string MessageId,
    string RequestId,
    DateTimeOffset Timestamp,
    string Model,
    string? Project,
    string? SessionId,
    long InputTokens,
    long OutputTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    long CacheReadTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheWrite5mTokens + CacheWrite1hTokens + CacheReadTokens;
}

/// <summary>Where the scanner stopped in one file, so the next scan reads only what was appended.</summary>
public sealed record ScanState(string Path, long Offset, long Length, long LastWriteTicks, string? Context = null);

public sealed record ScanResult(int Files, int ChangedFiles, int NewEvents, int SkippedFiles, TimeSpan Elapsed)
{
    public static ScanResult Empty { get; } = new(0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>Token counters for a set of messages.</summary>
public sealed record TokenTotals(long Input, long Output, long CacheWrite5m, long CacheWrite1h, long CacheRead, int Messages)
{
    public static TokenTotals Zero { get; } = new(0, 0, 0, 0, 0, 0);

    public long Total => Input + Output + CacheWrite5m + CacheWrite1h + CacheRead;

    public TokenTotals Add(TokenTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new TokenTotals(
            Input + other.Input,
            Output + other.Output,
            CacheWrite5m + other.CacheWrite5m,
            CacheWrite1h + other.CacheWrite1h,
            CacheRead + other.CacheRead,
            Messages + other.Messages);
    }
}

public sealed record ModelTotals(string Model, TokenTotals Tokens);

public sealed record DailyModelTotals(DateOnly Day, string Model, TokenTotals Tokens);

public sealed record ProjectUsage(string Project, long Tokens);

/// <summary>One recorded percentage for one window, the raw material of the history charts.</summary>
public sealed record HistoryPoint(DateTimeOffset Timestamp, double Percent, DateTimeOffset? ResetsAt);

/// <summary>Cost is null when no model in the period has a known price; <see cref="HasUnknownModels"/> flags a partial sum.</summary>
public sealed record ModelUsage(string Model, TokenTotals Tokens, decimal? Cost);

public sealed record PeriodAnalytics(
    DateTimeOffset From,
    DateTimeOffset To,
    TokenTotals Tokens,
    decimal? Cost,
    bool HasUnknownModels,
    IReadOnlyList<ModelUsage> ByModel);

/// <summary>The current 5-hour block: tokens burned since it started and, from the endpoint's percentage, when the limit lands.</summary>
public sealed record BlockAnalytics(
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Elapsed,
    TokenTotals Tokens,
    decimal? Cost,
    double TokensPerHour,
    double? PercentPerHour,
    DateTimeOffset? ProjectedLimitAt,
    bool LimitBeforeReset);

/// <summary>Everything derived from the local session logs at one moment. Never contains a percentage of its own.</summary>
public sealed record LocalAnalytics(
    DateTimeOffset ComputedAt,
    PeriodAnalytics Today,
    IReadOnlyList<ProjectUsage> TopProjectsToday,
    BlockAnalytics? CurrentBlock,
    PricingTable Pricing,
    long EventCount);
