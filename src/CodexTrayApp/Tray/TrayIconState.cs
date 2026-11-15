using System.Globalization;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Tray;

/// <summary>
/// What the icon shows: the primary window's percentage and status, whether the data is stale, and the weekly
/// window's percentage (null when the snapshot has none), which can override the numeral.
/// </summary>
public sealed record TrayIconState(double? Percent, UsageWindowStatus? Status, bool IsStale, double? WeeklyPercent = null)
{
    public static TrayIconState Unknown { get; } = new(null, null, false);

    /// <summary>The weekly limit is used up: the numeral is "!!!" and the ring black.</summary>
    public bool WeeklyExhausted => WeeklyPercent is >= 99.5;

    /// <summary>The weekly limit is in the critical band, but not yet used up: the numeral is "!".</summary>
    public bool WeeklyCritical => !WeeklyExhausted && WeeklyPercent is >= UsageWindowStatusRules.CriticalThreshold;

    /// <summary>The numeral: "!!!" when the weekly limit is out, "!" when it is critical or the window is at 100 %, "?" without data, otherwise the rounded percentage.</summary>
    public string Text => WeeklyExhausted
        ? "!!!"
        : WeeklyCritical
            ? "!"
            : Percent is not { } percent
                ? "?"
                : percent >= 99.5 ? "!" : Math.Round(percent).ToString(CultureInfo.InvariantCulture);
}
