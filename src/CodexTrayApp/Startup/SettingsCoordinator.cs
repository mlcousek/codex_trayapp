using System.Windows.Threading;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.History;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Theming;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Startup;

/// <summary>
/// Pushes settings into the services that cannot watch the store themselves: the poll interval, history retention,
/// the pricing file and the theme override. View models subscribe to the store on their own.
/// </summary>
public sealed class SettingsCoordinator : IDisposable
{
    private readonly SettingsStore _store;
    private readonly UsagePoller _poller;
    private readonly HistoryOptions _historyOptions;
    private readonly HistoryRecorder _recorder;
    private readonly JsonlScanner _scanner;
    private readonly PricingProvider _pricing;
    private readonly ThemeManager _theme;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SettingsCoordinator> _logger;

    public SettingsCoordinator(
        SettingsStore store,
        UsagePoller poller,
        HistoryOptions historyOptions,
        HistoryRecorder recorder,
        JsonlScanner scanner,
        PricingProvider pricing,
        ThemeManager theme,
        Dispatcher dispatcher,
        ILogger<SettingsCoordinator> logger)
    {
        _store = store;
        _poller = poller;
        _historyOptions = historyOptions;
        _recorder = recorder;
        _scanner = scanner;
        _pricing = pricing;
        _theme = theme;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Applies the current settings once, then follows the file.</summary>
    public void Start()
    {
        Apply(_store.Current, initial: true);
        _store.Changed += OnChanged;
        _store.StartWatching();
    }

    public void Dispose() => _store.Changed -= OnChanged;

    private static AppTheme? ToOverride(ThemeSetting theme) => theme switch
    {
        ThemeSetting.Light => AppTheme.Light,
        ThemeSetting.Dark => AppTheme.Dark,
        _ => null,
    };

    private void OnChanged(object? sender, AppSettings settings) => Apply(settings, initial: false);

    private void Apply(AppSettings settings, bool initial)
    {
        if (!initial)
        {
            _poller.UpdateOptions(settings.ToPollingOptions());
        }

        if (_historyOptions.RetentionDays != settings.HistoryRetentionDays)
        {
            _historyOptions.RetentionDays = settings.HistoryRetentionDays;
            _scanner.MaxAge = TimeSpan.FromDays(settings.HistoryRetentionDays);
            if (!initial)
            {
                _recorder.PruneNow();
            }
        }

        if (!string.Equals(_pricing.OverridePath, settings.PricingFilePath, StringComparison.OrdinalIgnoreCase))
        {
            _pricing.Reload(settings.PricingFilePath);
        }

        var themeOverride = ToOverride(settings.Theme);
        if (_dispatcher.CheckAccess())
        {
            ApplyTheme(themeOverride);
        }
        else
        {
            _dispatcher.BeginInvoke(() => ApplyTheme(themeOverride));
        }

        if (!initial)
        {
            _logger.LogInformation(
                "Settings applied: poll every {Interval} s, tray window {Window}, retention {Days} d, theme {Theme}, notifications {Notifications}",
                settings.PollIntervalSeconds,
                settings.TrayWindow,
                settings.HistoryRetentionDays,
                settings.Theme,
                settings.Notifications.Enabled ? string.Join("/", settings.Notifications.Thresholds) + "%" : "off");
        }
    }

    private void ApplyTheme(AppTheme? themeOverride)
    {
        if (_theme.Override != themeOverride)
        {
            _theme.Override = themeOverride;
            _theme.Apply();
        }
    }
}
