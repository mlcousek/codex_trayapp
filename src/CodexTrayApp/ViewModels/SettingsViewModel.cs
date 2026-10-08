using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.Core.Updates;
using CodexTrayApp.Startup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CodexTrayApp.ViewModels;

/// <summary>A value with the words shown for it in a picker.</summary>
public sealed record ChoiceItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The settings window's state. Every change is written to settings.json at once; the store's hot reload then
/// carries it to the rest of the app, and an edit made elsewhere (a text editor, another window) shows up here.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly IReadOnlyList<ChoiceItem<int>> ChartRangeChoices =
    [
        new(ChartsViewModel.DayRange, "24 hours"),
        new(ChartsViewModel.WeekRange, "7 days"),
        new(ChartsViewModel.MonthRange, "30 days"),
    ];

    public static readonly IReadOnlyList<ChoiceItem<ThemeSetting>> ThemeChoices =
    [
        new(ThemeSetting.System, "Follow Windows"),
        new(ThemeSetting.Light, "Light"),
        new(ThemeSetting.Dark, "Dark"),
    ];

    private readonly SettingsStore _store;
    private readonly IAutostartEntry _autostart;
    private readonly PricingProvider _pricing;
    private readonly UsagePoller _poller;
    private readonly LocalAnalyticsProvider _analytics;
    private readonly UpdateNotifier _updates;
    private readonly UpdateChecker _updateChecker;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly Action<string> _openReleasePage;
    private bool _loading;

    [ObservableProperty]
    private string _pollIntervalText = string.Empty;

    [ObservableProperty]
    private string? _pollIntervalNote;

    [ObservableProperty]
    private ChoiceItem<string>? _selectedTrayWindow;

    [ObservableProperty]
    private ChoiceItem<int>? _selectedChartRange;

    [ObservableProperty]
    private ChoiceItem<ThemeSetting>? _selectedTheme;

    [ObservableProperty]
    private bool _maskEmail = true;

    [ObservableProperty]
    private bool _showLocalAnalytics;

    [ObservableProperty]
    private bool _showExtraUsage;

    [ObservableProperty]
    private bool _showInactiveWindows;

    [ObservableProperty]
    private bool _checkForUpdates = true;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _retentionText = string.Empty;

    [ObservableProperty]
    private string? _retentionNote;

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private string _thresholdsText = string.Empty;

    [ObservableProperty]
    private string? _thresholdsNote;

    [ObservableProperty]
    private string _codexExecutableText = string.Empty;

    [ObservableProperty]
    private string _codexHomeText = string.Empty;

    [ObservableProperty]
    private string _pricingPathText = string.Empty;

    [ObservableProperty]
    private string _pricingStatus = string.Empty;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string? _startupNote;

    [ObservableProperty]
    private string? _fileError;

    [ObservableProperty]
    private string? _historyStatus;

    public SettingsViewModel(
        SettingsStore store,
        IAutostartEntry autostart,
        PricingProvider pricing,
        UsagePoller poller,
        IHistoryStore history,
        LocalAnalyticsProvider analytics,
        UpdateNotifier updates,
        UpdateChecker updateChecker,
        Dispatcher dispatcher,
        ILogger<SettingsViewModel> logger,
        Action<string>? openReleasePage = null)
    {
        _store = store;
        _autostart = autostart;
        _pricing = pricing;
        _poller = poller;
        _analytics = analytics;
        _updates = updates;
        _updateChecker = updateChecker;
        _dispatcher = dispatcher;
        _logger = logger;
        _openReleasePage = openReleasePage ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose());

        LoadFrom(_store.Current);
        RefreshAutostart();
        _store.Changed += OnSettingsChanged;
        _pricing.Changed += OnPricingChanged;
        _poller.StatusChanged += OnPollStatusChanged;
        _updates.Changed += OnUpdatesChanged;
        UpdateUpdateStatus();
    }

    /// <summary>The version this build reports, shown next to the update setting.</summary>
    public string CurrentVersionText => "Version " + _updateChecker.CurrentVersion;

    public ObservableCollection<ChoiceItem<string>> TrayWindowChoices { get; } = [];

    public string SettingsPath => _store.Path;

    public string SettingsFolder => Path.GetDirectoryName(_store.Path) ?? _store.Path;

    /// <summary>Copies the settings into the editable fields without writing anything back.</summary>
    public void LoadFrom(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loading = true;
        try
        {
            PollIntervalText = settings.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            PollIntervalNote = null;
            RefreshTrayWindowChoices(settings.TrayWindow);
            SelectedChartRange = ChartRangeChoices.FirstOrDefault(c => c.Value == settings.ChartRangeHours) ?? ChartRangeChoices[0];
            SelectedTheme = ThemeChoices.First(c => c.Value == settings.Theme);
            MaskEmail = settings.MaskEmail;
            ShowLocalAnalytics = settings.ShowLocalAnalytics;
            ShowExtraUsage = settings.ShowExtraUsage;
            ShowInactiveWindows = settings.ShowInactiveWindows;
            CheckForUpdates = settings.CheckForUpdates;
            RetentionText = settings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
            RetentionNote = null;
            NotificationsEnabled = settings.Notifications.Enabled;
            ThresholdsText = string.Join(", ", settings.Notifications.Thresholds);
            ThresholdsNote = null;
            CodexExecutableText = settings.CodexExecutablePath ?? string.Empty;
            CodexHomeText = settings.CodexHomePath ?? string.Empty;
            PricingPathText = settings.PricingFilePath ?? string.Empty;
            UpdatePricingStatus();
            FileError = _store.LastError;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Re-reads the Run entry, because it can change outside this window (the tray menu, Task Manager).</summary>
    public void RefreshAutostart()
    {
        _loading = true;
        try
        {
            StartWithWindows = _autostart.IsEnabled();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Whole seconds, raised to the floor when needed; letters are refused.</summary>
    internal static bool TryParseInterval(string? text, out int seconds, out string? note)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            seconds = 0;
            note = "Enter a whole number of seconds.";
            return false;
        }

        seconds = Math.Clamp(value, AppSettings.MinimumPollIntervalSeconds, AppSettings.MaximumPollIntervalSeconds);
        note = value < AppSettings.MinimumPollIntervalSeconds
            ? $"Raised to the {AppSettings.MinimumPollIntervalSeconds} s floor; the app limits background requests."
            : value > AppSettings.MaximumPollIntervalSeconds ? "Capped at one day." : null;
        return true;
    }

    internal static bool TryParseRetention(string? text, out int days, out string? note)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            days = 0;
            note = "Enter a whole number of days.";
            return false;
        }

        days = Math.Clamp(value, AppSettings.MinimumRetentionDays, AppSettings.MaximumRetentionDays);
        note = value != days ? $"Kept between {AppSettings.MinimumRetentionDays} and {AppSettings.MaximumRetentionDays} days." : null;
        return true;
    }

    /// <summary>Comma or space separated percentages between 1 and 100, sorted and deduplicated; the rest is reported.</summary>
    internal static List<int> ParseThresholds(string? text, out string? note)
    {
        var parts = (text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new List<int>();
        var rejected = new List<string>();
        foreach (var part in parts)
        {
            var token = part.TrimEnd('%');
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 100)
            {
                values.Add(value);
            }
            else
            {
                rejected.Add(part);
            }
        }

        var result = values.Distinct().Order().ToList();
        note = rejected.Count > 0
            ? "Ignored: " + string.Join(", ", rejected) + ". Use percentages between 1 and 100."
            : result.Count == 0 ? "No thresholds: nothing will be announced." : null;
        return result;
    }

    public void Dispose()
    {
        _store.Changed -= OnSettingsChanged;
        _pricing.Changed -= OnPricingChanged;
        _poller.StatusChanged -= OnPollStatusChanged;
        _updates.Changed -= OnUpdatesChanged;
    }

    /// <summary>What the last check found, in the words the settings window shows.</summary>
    internal static string DescribeUpdate(UpdateCheckOutcome? outcome, bool enabled) => (outcome, enabled) switch
    {
        (_, false) => "Update checks are off. Releases are listed on GitHub.",
        ({ Status: UpdateCheckStatus.Unavailable }, _) => "Update checks are unavailable because this build has no release repository configured.",
        (null, _) => "Not checked yet.",
        ({ Status: UpdateCheckStatus.UpdateAvailable, Release: { } release }, _) => $"Version {release.Version} is available.",
        ({ Status: UpdateCheckStatus.UpToDate }, _) => "This is the newest release.",
        _ => "The last check could not reach GitHub.",
    };

    [RelayCommand]
    private void ApplyCodexPaths()
    {
        ConnectionPathError = null;
        var executable = CodexExecutableText.Trim();
        var home = CodexHomeText.Trim();
        if (executable.Length > 0 && (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)))
        {
            FileError = ConnectionPathError = "Select an existing Codex .exe using an absolute path.";
            return;
        }
        if (home.Length > 0 && (!Path.IsPathFullyQualified(home) || !Directory.Exists(home)))
        {
            FileError = ConnectionPathError = "Select an existing Codex home directory using an absolute path.";
            return;
        }
        Persist(s => s with { CodexExecutablePath = executable.Length == 0 ? null : executable, CodexHomePath = home.Length == 0 ? null : home });
        _poller.RequestResumeRefresh();
    }

    [ObservableProperty]
    private string? _connectionPathError;

    [RelayCommand]
    private void BrowseCodexExecutable()
    {
        var dialog = new OpenFileDialog { Title = "Select Codex executable", Filter = "Windows executable (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() == true) { CodexExecutableText = dialog.FileName; }
    }

    [RelayCommand]
    private void BrowseCodexHome()
    {
        var dialog = new OpenFolderDialog { Title = "Select Codex home directory", Multiselect = false };
        if (dialog.ShowDialog() == true) { CodexHomeText = dialog.FolderName; }
    }

    partial void OnMaskEmailChanged(bool value) => Persist(s => s with { MaskEmail = value });

    partial void OnShowLocalAnalyticsChanged(bool value) => Persist(s => s with { ShowLocalAnalytics = value });

    partial void OnShowExtraUsageChanged(bool value) => Persist(s => s with { ShowExtraUsage = value });

    partial void OnShowInactiveWindowsChanged(bool value) => Persist(s => s with { ShowInactiveWindows = value });

    partial void OnCheckForUpdatesChanged(bool value)
    {
        Persist(s => s with { CheckForUpdates = value });
        UpdateUpdateStatus();
    }

    partial void OnNotificationsEnabledChanged(bool value) => Persist(s => s with { Notifications = s.Notifications with { Enabled = value } });

    partial void OnSelectedChartRangeChanged(ChoiceItem<int>? value)
    {
        if (value is not null)
        {
            Persist(s => s with { ChartRangeHours = value.Value });
        }
    }

    partial void OnSelectedThemeChanged(ChoiceItem<ThemeSetting>? value)
    {
        if (value is not null)
        {
            Persist(s => s with { Theme = value.Value });
        }
    }

    partial void OnSelectedTrayWindowChanged(ChoiceItem<string>? value)
    {
        if (value is not null)
        {
            Persist(s => s with { TrayWindow = value.Value });
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (_autostart.TrySet(value, out var error))
        {
            // The checkbox and the caption beside it already say what the state is; the note is for refusals only.
            StartupNote = null;
            return;
        }

        StartupNote = error;
        _loading = true;
        try
        {
            StartWithWindows = !value;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void ApplyPollInterval()
    {
        if (TryParseInterval(PollIntervalText, out var seconds, out var note))
        {
            Persist(s => s with { PollIntervalSeconds = seconds });
            PollIntervalText = seconds.ToString(CultureInfo.InvariantCulture);
        }

        PollIntervalNote = note;
    }

    [RelayCommand]
    private void ApplyRetention()
    {
        if (TryParseRetention(RetentionText, out var days, out var note))
        {
            Persist(s => s with { HistoryRetentionDays = days });
            RetentionText = days.ToString(CultureInfo.InvariantCulture);
        }

        RetentionNote = note;
    }

    [RelayCommand]
    private void ApplyThresholds()
    {
        var values = ParseThresholds(ThresholdsText, out var note);
        Persist(s => s with { Notifications = s.Notifications with { Thresholds = values } });
        ThresholdsText = string.Join(", ", values);
        ThresholdsNote = note;
    }

    [RelayCommand]
    private void ApplyPricingPath()
    {
        var path = PricingPathText.Trim();
        Persist(s => s with { PricingFilePath = path.Length == 0 ? null : path });
        UpdatePricingStatus();
    }

    [RelayCommand]
    private void UseBundledPricing()
    {
        PricingPathText = string.Empty;
        ApplyPricingPath();
    }

    [RelayCommand]
    private void BrowsePricing()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a pricing file",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            PricingPathText = dialog.FileName;
            ApplyPricingPath();
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _analytics.ClearHistoryAsync();
            HistoryStatus = "History cleared. New activity will be recorded; Codex session logs are unchanged.";
            _logger.LogInformation("History cleared from the settings window");
        }
        catch (Exception ex)
        {
            HistoryStatus = "History could not be cleared: " + ex.Message;
            _logger.LogWarning(ex, "History could not be cleared");
        }
    }

    private bool CanOpenReleasePage() => _updateChecker.IsConfigured;

    [RelayCommand(CanExecute = nameof(CanOpenReleasePage))]
    private void OpenReleasePage()
    {
        if (!CanOpenReleasePage())
        {
            return;
        }

        var url = _updates.Available?.Url ?? _updateChecker.ReleasesPageUrl;
        try
        {
            _openReleasePage(url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The releases page could not be opened");
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            Process.Start(new ProcessStartInfo(SettingsFolder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the settings folder");
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _store.Save(AppSettings.Default);
        LoadFrom(_store.Current);
    }

    private void Persist(Func<AppSettings, AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        _store.Update(change);
        FileError = _store.LastError;
    }

    private void UpdatePricingStatus()
    {
        var table = _pricing.Current;
        var date = table.EffectiveDate is { } d ? ", prices as of " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        PricingStatus = _pricing.OverrideFailed
            ? "That file could not be used; the bundled prices are in force (" + table.Models.Count + " models" + date + ")."
            : _pricing.OverridePath is null
                ? "Bundled file: " + table.Models.Count + " models" + date + "."
                : "Custom file: " + table.Models.Count + " models" + date + ".";
    }

    private void RefreshTrayWindowChoices(string? preferredKey = null)
    {
        var key = preferredKey ?? SelectedTrayWindow?.Value ?? _store.Current.TrayWindow;
        var choices = new List<ChoiceItem<string>> { new(WindowKeys.Auto, "Automatic (5-hour when available)") };
        var windows = _poller.Status.Snapshot?.Windows ?? [];
        choices.AddRange(windows.Select(w => new ChoiceItem<string>(w.Key, w.DisplayName)));
        if (!string.Equals(key, WindowKeys.Auto, StringComparison.OrdinalIgnoreCase)
            && !windows.Any(w => string.Equals(w.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new ChoiceItem<string>(key, WindowNameHumanizer.Humanize(key) + " (not in the last refresh)"));
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            if (!TrayWindowChoices.Select(c => c.Value).SequenceEqual(choices.Select(c => c.Value), StringComparer.OrdinalIgnoreCase))
            {
                TrayWindowChoices.Clear();
                foreach (var choice in choices)
                {
                    TrayWindowChoices.Add(choice);
                }
            }

            SelectedTrayWindow = TrayWindowChoices.FirstOrDefault(c => string.Equals(c.Value, key, StringComparison.OrdinalIgnoreCase)) ?? TrayWindowChoices[0];
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.BeginInvoke(() => LoadFrom(settings));

    private void OnPricingChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(UpdatePricingStatus);

    private void OnPollStatusChanged(object? sender, PollStatus status) => _dispatcher.BeginInvoke(() => RefreshTrayWindowChoices());

    private void OnUpdatesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(UpdateUpdateStatus);

    private void UpdateUpdateStatus()
    {
        UpdateStatus = !_updateChecker.IsConfigured && CheckForUpdates
            ? "Update checks are unavailable because this build has no release repository configured."
            : DescribeUpdate(_updates.Last, CheckForUpdates);
        HasUpdate = _updateChecker.IsConfigured && CheckForUpdates && _updates.Available is not null;
    }
}
