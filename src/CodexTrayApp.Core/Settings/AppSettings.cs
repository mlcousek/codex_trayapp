using System.Text.Json.Serialization;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;

namespace CodexTrayApp.Core.Settings;

public enum ThemeSetting
{
    System,
    Light,
    Dark,
}

/// <summary>Threshold notifications: off by default, and never more than once per window per period.</summary>
public sealed record NotificationSettings
{
    public static readonly IReadOnlyList<int> DefaultThresholds = [80, 95];

    public bool Enabled { get; init; }

    public IReadOnlyList<int> Thresholds { get; init; } = DefaultThresholds;

    public bool Equals(NotificationSettings? other) =>
        other is not null && Enabled == other.Enabled && Thresholds.SequenceEqual(other.Thresholds);

    public override int GetHashCode() => HashCode.Combine(Enabled, Thresholds.Count);
}

/// <summary>
/// Everything the user can change, as stored in settings.json. Immutable; <see cref="Normalized"/> applies the hard
/// limits (the poll floor above all) so a hand-edited file can never talk the app into misbehaving.
/// </summary>
public sealed record AppSettings
{
    public const string AutoTrayWindow = WindowKeys.Auto;
    public const int MinimumPollIntervalSeconds = 180;
    public const int MaximumPollIntervalSeconds = 24 * 60 * 60;
    public const int DefaultPollIntervalSeconds = 300;
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;
    public const int DefaultRetentionDays = 90;
    public static readonly IReadOnlyList<int> ChartRangeChoices = [24, 24 * 7, 24 * 30];

    public static AppSettings Default { get; } = new();

    /// <summary>Seconds between polls of the usage endpoint. The floor is a hard rule.</summary>
    public int PollIntervalSeconds { get; init; } = DefaultPollIntervalSeconds;

    /// <summary>Window key that drives the tray numeral, or "auto" for the 5-hour window when present.</summary>
    public string TrayWindow { get; init; } = AutoTrayWindow;

    /// <summary>History chart range in hours: 24, 168 or 720.</summary>
    public int ChartRangeHours { get; init; } = 24;

    public int HistoryRetentionDays { get; init; } = DefaultRetentionDays;

    public NotificationSettings Notifications { get; init; } = new();

    /// <summary>Show tokens, cost and pace from the session logs; off by default so the flyout starts with the endpoint's numbers only.</summary>
    public bool ShowLocalAnalytics { get; init; }

    /// <summary>Show the extra-usage (overage) line when the plan has it enabled; off by default.</summary>
    public bool ShowExtraUsage { get; init; }

    /// <summary>Show codename windows the endpoint reports at 0 % with no reset time; hidden by default.</summary>
    public bool ShowInactiveWindows { get; init; }

    public bool MaskEmail { get; init; } = true;

    /// <summary>
    /// Ask GitHub once a week whether a newer release exists, and say so in a notification. The only request that
    /// leaves this machine besides the usage endpoint; it carries nothing about the user and can be turned off here.
    /// </summary>
    public bool CheckForUpdates { get; init; } = true;

    public ThemeSetting Theme { get; init; } = ThemeSetting.System;

    /// <summary>Path to a pricing.json that replaces the bundled one; null or empty means bundled.</summary>
    public string? PricingFilePath { get; init; }
    public string? CodexExecutablePath { get; init; }
    public string? CodexHomePath { get; init; }

    [JsonIgnore]
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>Clamps every value into its allowed range and tidies the threshold list; idempotent.</summary>
    public AppSettings Normalized()
    {
        var thresholds = (Notifications?.Thresholds ?? NotificationSettings.DefaultThresholds)
            .Where(t => t is > 0 and <= 100)
            .Distinct()
            .Order()
            .ToList();

        return this with
        {
            PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, MinimumPollIntervalSeconds, MaximumPollIntervalSeconds),
            TrayWindow = string.IsNullOrWhiteSpace(TrayWindow) ? AutoTrayWindow : TrayWindow.Trim(),
            ChartRangeHours = ChartRangeChoices.Contains(ChartRangeHours) ? ChartRangeHours : ChartRangeChoices[0],
            HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, MinimumRetentionDays, MaximumRetentionDays),
            Notifications = new NotificationSettings { Enabled = Notifications?.Enabled ?? false, Thresholds = thresholds },
            PricingFilePath = string.IsNullOrWhiteSpace(PricingFilePath) ? null : PricingFilePath.Trim(),
        };
    }

    /// <summary>The polling options these settings ask for; the poller applies its own floor again.</summary>
    public PollingOptions ToPollingOptions() => new() { Interval = PollInterval };
}
