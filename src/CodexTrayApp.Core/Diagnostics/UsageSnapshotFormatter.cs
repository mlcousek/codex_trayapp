using System.Globalization;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Core.Diagnostics;

/// <summary>Plain-text rendering of a snapshot for logs, the probe mode and tooltips. Contains no secrets.</summary>
public static class UsageSnapshotFormatter
{
    public static string Describe(UsageSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var windows = snapshot.Windows.Count == 0
            ? "no usage windows"
            : string.Join(" | ", snapshot.Windows.Select(w => DescribeWindow(w, now)));

        var overage = snapshot.Overage is { IsEnabled: true } o
            ? string.Create(CultureInfo.InvariantCulture, $" | extra usage {FormatAmount(o.UsedAmount)}/{FormatAmount(o.LimitAmount)} {o.Currency}")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{windows}{overage} | plan: {snapshot.PlanTier ?? "unknown"} | source: {snapshot.Source} | updated: {snapshot.LastUpdated:O}");
    }

    public static string DescribeWindow(UsageWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        var percent = window.UtilizationPercent.ToString("0.#", CultureInfo.InvariantCulture);
        var locked = window.IsLocked ? ", locked: " + window.LockedReason : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{window.DisplayName} {percent}% ({FormatReset(window, now)}{locked})");
    }

    /// <summary>"1d 3h", "1h 26m" or "12m". Never shows seconds; a countdown that jitters is noise.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalDays}d {duration.Hours}h");
        }

        if (duration.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m");
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}m");
    }

    private static string FormatReset(UsageWindow window, DateTimeOffset now) =>
        window.TimeUntilReset(now) is { } left
            ? "resets in " + FormatDuration(left)
            : window.ResetsAt is null ? "no reset time" : "reset due";

    private static string FormatAmount(decimal? amount) =>
        amount?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
}
