using System.Globalization;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Tray;

/// <summary>What the icon shows: the primary window's percentage and status, and whether the data is stale.</summary>
public sealed record TrayIconState(double? Percent, UsageWindowStatus? Status, bool IsStale)
{
    public static TrayIconState Unknown { get; } = new(null, null, false);

    /// <summary>The numeral: "?" without data, "!" at 100 %, otherwise the rounded percentage.</summary>
    public string Text => Percent is not { } percent
        ? "?"
        : percent >= 99.5 ? "!" : Math.Round(percent).ToString(CultureInfo.InvariantCulture);
}
