using System.Text.Json.Serialization;

namespace CodexTrayApp.Core.Domain;

/// <summary>Where a snapshot came from.</summary>
public enum UsageSource
{
    Live,
    Cache,
}

/// <summary>
/// Coarse state of a usage window, derived from its utilisation.
/// The UI maps this to colour and to text, never to colour alone.
/// </summary>
public enum UsageWindowStatus
{
    Ok,
    Warning,
    Critical,
    Exhausted,
}

/// <summary>One rate-limit window as reported by the usage endpoint, for example the 5-hour or 7-day window.</summary>
public sealed record UsageWindow(
    string Key,
    string DisplayName,
    double UtilizationPercent,
    DateTimeOffset? ResetsAt,
    UsageWindowStatus Status,
    string? LockedReason = null)
{
    public long? DurationMinutes { get; init; }

    /// <summary>Builds a window from raw values: clamps the percentage, humanises the key and derives the status.</summary>
    public static UsageWindow Create(string key, double utilizationPercent, DateTimeOffset? resetsAt, string? displayName = null, string? lockedReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var percent = double.IsFinite(utilizationPercent) ? Math.Clamp(utilizationPercent, 0, 100) : 0;
        return new UsageWindow(
            key,
            displayName ?? WindowNameHumanizer.Humanize(key),
            percent,
            resetsAt,
            UsageWindowStatusRules.FromPercent(percent),
            string.IsNullOrWhiteSpace(lockedReason) ? null : lockedReason);
    }

    /// <summary>True when the endpoint reports the window as locked, whatever the percentage says.</summary>
    public bool IsLocked => LockedReason is not null;

    /// <summary>A window at 0 % with no reset time carries nothing to show; the endpoint returns codename windows like that.</summary>
    public bool IsInactive => UtilizationPercent <= 0 && ResetsAt is null && !IsLocked;

    /// <summary>True for the documented keys (5-hour, 7-day and its per-model variants), which are always shown.</summary>
    public bool IsKnownKey => WindowKeys.PreferredOrder.Any(k => string.Equals(k, Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Time left until the window resets, or null when unknown or already due.</summary>
    public TimeSpan? TimeUntilReset(DateTimeOffset now) => ResetsAt is { } resets && resets > now ? resets - now : null;
}

/// <summary>Extra-usage (overage) balance, when the plan has it enabled. Amounts are in <see cref="Currency"/>.</summary>
public sealed record OverageInfo(
    bool IsEnabled,
    decimal? UsedAmount,
    decimal? LimitAmount,
    double? UtilizationPercent,
    string? Currency);

/// <summary>
/// Everything the usage endpoint told us at one point in time.
/// Providers produce it and the UI consumes it; neither knows about the other.
/// </summary>
public sealed record UsageSnapshot(
    IReadOnlyList<UsageWindow> Windows,
    string? PlanTier,
    string? AccountEmail,
    OverageInfo? Overage,
    DateTimeOffset LastUpdated,
    UsageSource Source)
{
    public string? AccountKey { get; init; }
    /// <summary>Hash of the verified account-read identity/source, used to confirm a restored cache before quota returns.</summary>
    public string? AccountIdentityKey { get; init; }
    /// <summary>Hash of the resolved Codex home, checked before connecting to a restored cache's source.</summary>
    public string? AccountSourceKey { get; init; }
    public string? CreditsText { get; init; }

    public static UsageSnapshot Empty(DateTimeOffset now) => new([], null, null, null, now, UsageSource.Live);

    public UsageWindow? FindWindow(string key) =>
        Windows.FirstOrDefault(w => string.Equals(w.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The window that drives the tray numeral when the user has not chosen one: the 5-hour window if present, else the first.</summary>
    [JsonIgnore]
    public UsageWindow? PrimaryWindow => FindWindow(WindowKeys.FiveHour) ?? (Windows.Count > 0 ? Windows[0] : null);

    /// <summary>
    /// The windows worth a line: every documented window, plus codename windows only while they carry a value or a
    /// reset time, unless <paramref name="showInactive"/> asks for all of them.
    /// </summary>
    public IReadOnlyList<UsageWindow> VisibleWindows(bool showInactive) =>
        showInactive ? Windows : Windows.Where(w => w.IsKnownKey || !w.IsInactive).ToList();

    /// <summary>The window a setting names, or <see cref="PrimaryWindow"/> for "auto", an empty key, or a key this snapshot lacks.</summary>
    public UsageWindow? WindowFor(string? preferredKey) =>
        string.IsNullOrWhiteSpace(preferredKey) || string.Equals(preferredKey, WindowKeys.Auto, StringComparison.OrdinalIgnoreCase)
            ? PrimaryWindow
            : FindWindow(preferredKey);
}

/// <summary>Window keys the endpoint is known to use. Unknown keys still render; these only drive ordering and defaults.</summary>
public static class WindowKeys
{
    /// <summary>Setting value meaning "let the app choose": the 5-hour window when present, else the first.</summary>
    public const string Auto = "auto";

    public const string FiveHour = "codex:primary";
    public const string SevenDay = "codex:secondary";
    public const string SevenDayOpus = "codex_other:primary";
    public const string SevenDaySonnet = "codex_other:secondary";
    public const string SevenDayOAuthApps = "codex_extra:primary";

    public static IReadOnlyList<string> PreferredOrder { get; } = [FiveHour, SevenDay, SevenDayOpus, SevenDaySonnet, SevenDayOAuthApps];
}

/// <summary>Thresholds that turn a percentage into a status.</summary>
public static class UsageWindowStatusRules
{
    public const double WarningThreshold = 70;
    public const double CriticalThreshold = 90;

    public static UsageWindowStatus FromPercent(double percent) => percent switch
    {
        >= 100 => UsageWindowStatus.Exhausted,
        >= CriticalThreshold => UsageWindowStatus.Critical,
        >= WarningThreshold => UsageWindowStatus.Warning,
        _ => UsageWindowStatus.Ok,
    };
}
