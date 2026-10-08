namespace CodexTrayApp.Core.Domain;

/// <summary>Unix epoch helpers for values whose unit (seconds or milliseconds) is not guaranteed by the source.</summary>
public static class EpochTime
{
    /// <summary>Anything below 10^11 cannot be a millisecond timestamp after 1973, so it is read as seconds.</summary>
    private const long MillisecondThreshold = 100_000_000_000;

    public static DateTimeOffset FromUnixSecondsOrMilliseconds(long value) =>
        value < MillisecondThreshold
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : DateTimeOffset.FromUnixTimeMilliseconds(value);
}
