using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CodexTrayApp.Core;
using CodexTrayApp.Core.Diagnostics;
using CodexTrayApp.Core.Notifications;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Startup;
using CodexTrayApp.Tray;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.ViewModels;

/// <summary>A short message for the user, shown as a Windows notification from the tray icon.</summary>
public sealed record TrayNotification(string Title, string Message, bool IsWarning);

/// <summary>State behind the tray icon: what to draw, what the tooltip says, and the context-menu commands.</summary>
public sealed partial class TrayIconViewModel : ObservableObject, IDisposable
{
    /// <summary>Shell tooltips are cut at 127 characters; stay comfortably below.</summary>
    public const int TooltipLimit = 120;

    private readonly UsagePoller _poller;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ThresholdNotifier _notifier;
    private readonly IAutostartEntry _autostart;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly string _version;
    private readonly ILogger<TrayIconViewModel> _logger;
    private bool _loadingAutostart;
    private bool _disposed;

    [ObservableProperty]
    private TrayIconState _iconState = TrayIconState.Unknown;

    [ObservableProperty]
    private string _tooltip = "Codex Usage Tray: waiting for the first refresh";

    [ObservableProperty]
    private bool _startWithWindows;

    public TrayIconViewModel(
        UsagePoller poller,
        AppPaths paths,
        SettingsStore settings,
        ThresholdNotifier notifier,
        IAutostartEntry autostart,
        TimeProvider clock,
        Dispatcher dispatcher,
        Action openSettings,
        Action quit,
        string version,
        ILogger<TrayIconViewModel> logger)
    {
        _poller = poller;
        _paths = paths;
        _settings = settings;
        _notifier = notifier;
        _autostart = autostart;
        _clock = clock;
        _dispatcher = dispatcher;
        _openSettings = openSettings;
        _quit = quit;
        _version = version;
        _logger = logger;

        _poller.StatusChanged += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
        Apply(_poller.Status);
        RefreshAutostart();
    }

    /// <summary>Raised when the user should see a short message: a refused refresh, a threshold crossed.</summary>
    public event EventHandler<TrayNotification>? NotificationRequested;

    /// <summary>Projects a poll status onto the icon state and tooltip, and announces crossed thresholds. Must run on the UI thread.</summary>
    public void Apply(PollStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_disposed) { return; }
        var settings = _settings.Current;
        var primary = status.Snapshot?.WindowFor(settings.TrayWindow);
        var stale = status.IsStale || status.State is PollState.Idle or PollState.Stale or PollState.RateLimited or PollState.Unauthenticated;
        var now = _clock.GetUtcNow();

        IconState = primary is null
            ? TrayIconState.Unknown with { IsStale = stale }
            : new TrayIconState(primary.UtilizationPercent, primary.Status, stale);
        Tooltip = BuildTooltip(status, now, settings.ShowInactiveWindows);

        if (status.State == PollState.Ok && status.Snapshot is { } snapshot && settings.Notifications.Enabled)
        {
            foreach (var alert in _notifier.Evaluate(snapshot, settings.Notifications.Thresholds, now))
            {
                _logger.LogInformation("Threshold notification: {Window} at {Percent}% passed {Threshold}%", alert.WindowKey, Math.Round(alert.Percent), alert.Threshold);
                NotificationRequested?.Invoke(this, new TrayNotification("Codex usage", FormatAlert(alert, now), IsWarning: true));
            }
        }
    }

    /// <summary>Re-reads the Run entry; the menu calls this when it opens because the settings window can change it too.</summary>
    public void RefreshAutostart()
    {
        _loadingAutostart = true;
        try
        {
            StartWithWindows = _autostart.IsEnabled();
        }
        finally
        {
            _loadingAutostart = false;
        }
    }

    /// <summary>"5-hour usage is at 86%, past your 80% mark. Resets in 1h 53m."</summary>
    internal static string FormatAlert(ThresholdAlert alert, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var reset = alert.ResetsAt is { } at && at > now ? " Resets in " + UsageSnapshotFormatter.FormatDuration(at - now) + "." : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{alert.WindowName} usage is at {Math.Round(alert.Percent)}%, past your {alert.Threshold}% mark.{reset}");
    }

    /// <summary>One line: the first window with its reset countdown, up to two more windows, then the state.</summary>
    internal static string BuildTooltip(PollStatus status, DateTimeOffset now, bool showInactiveWindows = false)
    {
        string text;
        if (status.Snapshot is { } snapshot && snapshot.VisibleWindows(showInactiveWindows) is { Count: > 0 } windows)
        {
            var parts = windows.Take(3).Select((window, index) => index == 0
                ? UsageSnapshotFormatter.DescribeWindow(window, now)
                : string.Create(CultureInfo.InvariantCulture, $"{window.DisplayName} {Math.Round(window.UtilizationPercent)}%"));
            var suffix = Suffix(status.State);
            var prefix = status.State is PollState.Ok or PollState.Idle ? "Codex usage: " : "Codex usage (" + ConnectionStatusText.Label(status).ToLowerInvariant() + "): ";
            var body = string.Join(" · ", parts);
            var budget = TooltipLimit - prefix.Length - suffix.Length;
            if (body.Length > budget) { body = body[..(budget - 1)] + "…"; }
            text = prefix + body + suffix;
        }
        else
        {
            text = "Codex Usage Tray: " + (status.Message ?? "no data yet");
        }

        return text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
    }

    public void Dispose()
    {
        _disposed = true;
        _poller.StatusChanged -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingAutostart)
        {
            return;
        }

        if (!_autostart.TrySet(value, out var error))
        {
            NotificationRequested?.Invoke(this, new TrayNotification("Codex Usage Tray", error ?? "The startup entry could not be changed.", IsWarning: true));
            RefreshAutostart();
        }
    }

    private static string Suffix(PollState state) => state switch
    {
        PollState.RateLimited => " · rate limited",
        PollState.Unauthenticated => " · sign in with Codex CLI",
        PollState.Stale => " · stale",
        PollState.Idle => " · cached",
        _ => string.Empty,
    };

    [RelayCommand]
    private void Refresh()
    {
        if (_poller.TryRequestRefresh(out var reason))
        {
            _logger.LogInformation("Manual refresh requested from the tray menu");
            return;
        }

        NotificationRequested?.Invoke(this, new TrayNotification("Codex Usage Tray", reason ?? "Refresh is not possible right now.", IsWarning: false));
    }

    [RelayCommand]
    private void Settings() => _openSettings();

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the logs folder");
        }
    }

    [RelayCommand]
    private void About()
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Codex Usage Tray {_version}\n\nAn unofficial, open-source tray app that shows your Codex usage limits.\nNot affiliated with or endorsed by OpenAI.\n\nhttps://github.com/mlcousek/codex_trayapp\n\nLogs: {_paths.LogsDirectory}");
        MessageBox.Show(text, "About Codex Usage Tray", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Quit()
    {
        _logger.LogInformation("Quit requested from the tray menu");
        _quit();
    }

    private void OnStatusChanged(object? sender, PollStatus status) => _dispatcher.BeginInvoke(() => Apply(status));

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.BeginInvoke(() => Apply(_poller.Status));
}
