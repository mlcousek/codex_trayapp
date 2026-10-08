using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CodexTrayApp.Charts;
using CodexTrayApp.Core.Account;
using CodexTrayApp.Core.Aggregation;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Diagnostics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.ViewModels;

/// <summary>A name, a value and an optional detail: one line in the details disclosure.</summary>
public sealed record UsageRowViewModel(string Name, string Value, string? Detail);

/// <summary>Everything the flyout binds to. Formatting happens here; the view only binds and draws.</summary>
public sealed partial class FlyoutViewModel : ObservableObject, IDisposable
{
    private readonly UsagePoller _poller;
    private readonly IAccountInfoSource _accountSource;
    private readonly LocalAnalyticsProvider _analytics;
    private readonly AnalyticsCalculator _calculator;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<FlyoutViewModel> _logger;
    private readonly ChartDataLoader _chartLoader;
    private readonly SettingsStore _settings;
    private readonly PricingProvider _pricing;
    private readonly Action _openSettings;
    private readonly IHistoryStore _history;
    private bool _showLocalAnalytics;
    private bool _showExtraUsage;
    private bool _showInactiveWindows;
    private bool _applyingSettings;
    private PollStatus _status;
    private AccountInfo _account = AccountInfo.Empty;
    private LocalAnalytics? _local;
    private bool _visible;
    private bool _chartsLoading;
    private bool _chartsDirty;
    private long _chartGeneration;
    private bool _disposed;

    [ObservableProperty]
    private string _planTierText = "Codex";

    [ObservableProperty]
    private string _accountText = string.Empty;

    [ObservableProperty]
    private bool _hasAccount;

    [ObservableProperty]
    private bool _hasEmail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskToggleLabel))]
    private bool _isEmailMasked = true;

    [ObservableProperty]
    private UsageWindowViewModel? _primary;

    [ObservableProperty]
    private bool _hasPrimary;

    [ObservableProperty]
    private bool _hasSecondary;

    [ObservableProperty]
    private bool _hasWindows;

    [ObservableProperty]
    private bool _hasNoWindows = true;

    [ObservableProperty]
    private string _unavailableText = "Percentages unavailable. Waiting for the first refresh.";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasStatusMessage;

    [ObservableProperty]
    private bool _hasConnectionAction;

    [ObservableProperty]
    private string? _overageText;

    [ObservableProperty]
    private string _overageAmountText = string.Empty;

    [ObservableProperty]
    private double _overagePercent;

    [ObservableProperty]
    private bool _hasOverage;

    [ObservableProperty]
    private bool _hasPace;

    [ObservableProperty]
    private string _paceText = string.Empty;

    [ObservableProperty]
    private bool _hasProjection;

    [ObservableProperty]
    private string _projectionText = string.Empty;

    [ObservableProperty]
    private bool _hasToday;

    [ObservableProperty]
    private string _todayTokensText = string.Empty;

    [ObservableProperty]
    private string _todayCostText = string.Empty;

    [ObservableProperty]
    private string _pricingNoteText = string.Empty;

    [ObservableProperty]
    private bool _hasProjects;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsToggleLabel))]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private bool _hasAnalyticsNote;

    [ObservableProperty]
    private string _analyticsNoteText = string.Empty;

    [ObservableProperty]
    private string _sourcesText = string.Empty;

    [ObservableProperty]
    private string _lastUpdatedText = "No data yet";

    [ObservableProperty]
    private string? _refreshFeedback;

    [ObservableProperty]
    private bool _hasRefreshFeedback;

    public FlyoutViewModel(
        UsagePoller poller,
        IAccountInfoSource accountSource,
        LocalAnalyticsProvider analytics,
        AnalyticsCalculator calculator,
        IHistoryStore history,
        SettingsStore settings,
        PricingProvider pricing,
        TimeProvider clock,
        Dispatcher dispatcher,
        Action openSettings,
        ILogger<FlyoutViewModel> logger)
    {
        _poller = poller;
        _history = history;
        _accountSource = accountSource;
        _analytics = analytics;
        _calculator = calculator;
        _settings = settings;
        _pricing = pricing;
        _clock = clock;
        _dispatcher = dispatcher;
        _openSettings = openSettings;
        _logger = logger;
        _status = poller.Status;
        _chartLoader = new ChartDataLoader(history, calculator, clock.LocalTimeZone, logger);
        Charts = new ChartsViewModel(ChartPalette.FromApplication(), clock.LocalTimeZone);
        ApplySettings(settings.Current);
        Charts.RangeChanged += OnChartRangeChanged;

        _poller.StatusChanged += OnStatusChanged;
        _analytics.DataChanged += OnAnalyticsChanged;
        _settings.Changed += OnSettingsChanged;
        _pricing.Changed += OnPricingChanged;
        Rebuild();
    }

    /// <summary>The charts section; refreshed from the history database only while the flyout is visible.</summary>
    public ChartsViewModel Charts { get; }

    /// <summary>Every window except the primary one, in endpoint order.</summary>
    public ObservableCollection<UsageWindowViewModel> SecondaryWindows { get; } = [];

    public ObservableCollection<UsageRowViewModel> ModelRows { get; } = [];

    public ObservableCollection<UsageRowViewModel> ProjectRows { get; } = [];

    public string MaskToggleLabel => IsEmailMasked ? "Show" : "Hide";

    public string DetailsToggleLabel => IsDetailsOpen ? "Hide details" : "Details by model and project";

    /// <summary>Hides the account line entirely; used when producing screenshots.</summary>
    public bool AccountHidden { get; set; }

    /// <summary>Reads the account block once. Safe to call again; failures leave the header without an email.</summary>
    public async Task LoadAccountAsync(CancellationToken cancellationToken)
    {
        var generation = _chartGeneration;
        AccountInfo account;
        try
        {
            account = await _accountSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Account info could not be read");
            account = AccountInfo.Empty;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || generation != _chartGeneration) { return; }
            _account = account;
            UpdateHeader();
        });
    }

    /// <summary>Projects a poll status onto the bindable state. Must run on the UI thread.</summary>
    public void Apply(PollStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_disposed) { return; }
        _chartGeneration++;
        if (!string.Equals(_status.Snapshot?.AccountKey, status.Snapshot?.AccountKey, StringComparison.Ordinal) || (_status.Snapshot is not null && status.Snapshot is null))
        {
            ClearQuotaCharts();
        }
        _status = status;
        if (status.State is PollState.Ok or PollState.Stale or PollState.RateLimited or PollState.Unauthenticated)
        {
            ClearFeedback();
        }

        Rebuild();
    }

    /// <summary>Re-renders time-dependent text (countdowns, "updated 3 min ago") without new data.</summary>
    public void Tick() => Rebuild();

    /// <summary>The window became visible: refresh text now and keep charts current on every change.</summary>
    public void OnShown()
    {
        _visible = true;
        Rebuild();
        RefreshCharts(force: true);
    }

    /// <summary>The window was hidden: charts stop refreshing until the next open.</summary>
    public void OnHidden() => _visible = false;

    /// <summary>Loads chart data once while hidden, so the first open already has lines to draw.</summary>
    public void PreloadCharts() => RefreshCharts(force: true);

    /// <summary>"max" becomes "Codex Max"; tier strings such as default_codex_max_5x become "Codex Max 5x".</summary>
    internal static string FormatPlan(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return "Codex";
        }

        const string TierPrefix = "default_codex_";
        var name = tier.StartsWith(TierPrefix, StringComparison.OrdinalIgnoreCase) ? tier[TierPrefix.Length..] : tier;
        var words = name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select((word, index) => index == 0 ? Capitalize(word) : word.ToLower(CultureInfo.InvariantCulture));
        return "Codex " + string.Join(' ', words);
    }

    /// <summary>The last folder of a project path; empty or unknown paths become "(unknown)".</summary>
    internal static string ProjectLabel(string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return "(unknown)";
        }

        var trimmed = project.TrimEnd('/', '\\');
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
    }

    public void Dispose()
    {
        _disposed = true;
        _chartGeneration++;
        _poller.StatusChanged -= OnStatusChanged;
        _analytics.DataChanged -= OnAnalyticsChanged;
        _settings.Changed -= OnSettingsChanged;
        _pricing.Changed -= OnPricingChanged;
        Charts.RangeChanged -= OnChartRangeChanged;
    }

    /// <summary>Takes the flyout-related settings without writing anything back.</summary>
    private void ApplySettings(AppSettings settings)
    {
        _chartGeneration++;
        _applyingSettings = true;
        try
        {
            IsEmailMasked = settings.MaskEmail;
            _showLocalAnalytics = settings.ShowLocalAnalytics;
            _showExtraUsage = settings.ShowExtraUsage;
            _showInactiveWindows = settings.ShowInactiveWindows;
            Charts.ShowDaily = settings.ShowLocalAnalytics;
            Charts.RangeHours = settings.ChartRangeHours;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..].ToLower(CultureInfo.InvariantCulture);

    private void Rebuild()
    {
        if (_disposed) { return; }
        var now = _clock.GetUtcNow();
        var snapshot = _status.Snapshot;
        if (_history is SqliteStore sqlite) { sqlite.ActiveAccountKey = snapshot?.AccountKey; }
        _local = ComputeAnalytics(_status.IsStale && snapshot is not null ? snapshot with { Source = UsageSource.Cache } : snapshot, now);
        var aggregated = UsageAggregator.Combine(_status, _local, now);

        RebuildWindows(snapshot, now);
        RebuildOverage(snapshot);
        RebuildAnalytics(aggregated);

        UnavailableText = "Percentages unavailable.";
        StatusMessage = ConnectionStatusText.Banner(_status, now);
        HasStatusMessage = StatusMessage is not null;
        HasConnectionAction = _status.State is PollState.Stale or PollState.Unauthenticated;
        LastUpdatedText = _status.LastSuccess is { } success
            ? (_status.IsStale ? "Cached, updated " : "Updated ") + RelativeTime.Format(success, now)
            : "No data yet";
        SourcesText = (aggregated.PercentagesAvailable, _showLocalAnalytics) switch
        {
            (true, true) => $"Percentages: {aggregated.PercentagesSource}; tokens: local sessions",
            (true, false) => $"Percentages: {aggregated.PercentagesSource}",
            (false, true) => "Local tokens: local sessions; percentages unavailable",
            _ => "Percentages unavailable",
        };

        _account = snapshot is null ? AccountInfo.Empty : new AccountInfo(snapshot.AccountEmail, null, null, null, snapshot.PlanTier, null);
        UpdateHeader();
        RefreshCharts(force: false);
    }

    /// <summary>
    /// Loads chart data on a thread-pool thread and applies it on the dispatcher. Calls made while a load is running
    /// are folded into one more load, so a burst of changes costs two queries at most.
    /// </summary>
    private void RefreshCharts(bool force)
    {
        if (_disposed || (!force && !_visible))
        {
            return;
        }

        if (_chartsLoading)
        {
            _chartsDirty = true;
            return;
        }

        _chartsLoading = true;
        _ = LoadChartsAsync(_status.Snapshot, _local?.CurrentBlock, Charts.RangeHours, _showInactiveWindows, _clock.GetUtcNow(), _chartGeneration);
    }

    private async Task LoadChartsAsync(UsageSnapshot? snapshot, BlockAnalytics? block, int rangeHours, bool showInactiveWindows, DateTimeOffset now, long generation)
    {
        try
        {
            var bundle = await Task.Run(() => _chartLoader.Load(snapshot, block, rangeHours, now, showInactiveWindows));
            await _dispatcher.InvokeAsync(() => TryApplyCharts(bundle, generation, snapshot?.AccountKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chart data could not be loaded");
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _chartsLoading = false;
                if (_chartsDirty)
                {
                    _chartsDirty = false;
                    RefreshCharts(force: true);
                }
            });
        }
    }

    internal long ChartGeneration => _chartGeneration;

    internal bool TryApplyCharts(ChartBundle bundle, long generation, string? accountKey)
    {
        if (_disposed || generation != _chartGeneration || !string.Equals(accountKey, _status.Snapshot?.AccountKey, StringComparison.Ordinal)) { return false; }
        Charts.Apply(bundle);
        Primary?.SetSpark(bundle.Sparklines.GetValueOrDefault(Primary.Key));
        foreach (var row in SecondaryWindows)
        {
            row.SetSpark(bundle.Sparklines.GetValueOrDefault(row.Key));
        }
        return true;
    }

    private void ClearQuotaCharts()
    {
        var now = _clock.GetUtcNow();
        Charts.ApplyBlock(null);
        Charts.ApplyHistory(ChartDataBuilder.History([], now - TimeSpan.FromHours(Charts.RangeHours), now));
        Charts.HasAnyData = Charts.HasDaily;
        Primary?.SetSpark(null);
        foreach (var row in SecondaryWindows) { row.SetSpark(null); }
    }

    private LocalAnalytics? ComputeAnalytics(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        try
        {
            return _calculator.Compute(snapshot, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local analytics could not be computed");
            return null;
        }
    }

    private void RebuildWindows(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var primary = snapshot?.PrimaryWindow;
        if (primary is null)
        {
            Primary = null;
        }
        else if (Primary is { } existing && string.Equals(existing.Key, primary.Key, StringComparison.OrdinalIgnoreCase))
        {
            existing.Update(primary, now);
        }
        else
        {
            Primary = new UsageWindowViewModel(primary, now);
        }

        HasPrimary = Primary is not null;
        SyncSecondary(snapshot?.VisibleWindows(_showInactiveWindows).Where(w => !ReferenceEquals(w, primary)).ToList() ?? [], now);
        HasSecondary = SecondaryWindows.Count > 0;
        HasWindows = HasPrimary || HasSecondary;
        HasNoWindows = !HasWindows;
    }

    private void RebuildOverage(UsageSnapshot? snapshot)
    {
        HasOverage = _showExtraUsage && snapshot?.CreditsText is not null;
        OverageAmountText = snapshot?.CreditsText ?? string.Empty;
        OverageText = OverageAmountText;
        OveragePercent = 0;
    }

    private void RebuildAnalytics(AggregatedUsage aggregated)
    {
        if (!_showLocalAnalytics)
        {
            HasPace = false;
            HasProjection = false;
            HasToday = false;
            HasProjects = false;
            HasAnalyticsNote = false;
            ModelRows.Clear();
            ProjectRows.Clear();
            return;
        }

        var local = aggregated.Local;
        var block = local?.CurrentBlock;
        HasPace = block is not null;
        if (block is not null)
        {
            var cost = block.Cost is { } c ? " · ≈ " + TokenFormat.Money(c, local!.Pricing.Currency) : string.Empty;
            PaceText = $"{TokenFormat.Compact(block.Tokens.Total)} tokens this block · {TokenFormat.Compact(block.TokensPerHour)}/h{cost}";
            ProjectionText = block switch
            {
                { ProjectedLimitAt: { } at, LimitBeforeReset: true } when at > aggregated.Now =>
                    "At this pace the limit lands at " + at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) + ", before the reset",
                { ProjectedLimitAt: not null, LimitBeforeReset: false } => "At this pace the window resets before the limit",
                _ => string.Empty,
            };
            HasProjection = ProjectionText.Length > 0;
        }
        else
        {
            PaceText = string.Empty;
            ProjectionText = string.Empty;
            HasProjection = false;
        }

        HasToday = local is { EventCount: > 0 };
        if (local is not null && HasToday)
        {
            var today = local.Today;
            TodayTokensText = TokenFormat.Compact(today.Tokens.Total) + " tokens";
            TodayCostText = today.Cost is { } cost
                ? "≈ " + TokenFormat.Money(cost, local.Pricing.Currency) + " at API rates" + (today.HasUnknownModels ? ", some models unpriced" : string.Empty)
                : "Cost unknown: no priced models today";
            PricingNoteText = local.Pricing.EffectiveDate is { } date
                ? "Prices as of " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "No pricing file loaded";

            ReplaceRows(ModelRows, today.ByModel.Select(m => new UsageRowViewModel(
                m.Model,
                TokenFormat.Compact(m.Tokens.Total),
                m.Cost is { } modelCost ? TokenFormat.Money(modelCost, local.Pricing.Currency) : "cost unknown")));
            ReplaceRows(ProjectRows, local.TopProjectsToday.Select(p => new UsageRowViewModel(ProjectLabel(p.Project), TokenFormat.Compact(p.Tokens), null)));
            HasProjects = ProjectRows.Count > 0;
        }
        else
        {
            TodayTokensText = string.Empty;
            TodayCostText = string.Empty;
            PricingNoteText = string.Empty;
            ModelRows.Clear();
            ProjectRows.Clear();
            HasProjects = false;
        }

        AnalyticsNoteText = local switch
        {
            null => "Local analytics unavailable; see the log.",
            { EventCount: 0 } => _analytics.ProjectsDirectoryExists
                ? "Scanning Codex CLI session logs…"
                : "No Codex CLI session logs found yet.",
            _ => string.Empty,
        };
        HasAnalyticsNote = AnalyticsNoteText.Length > 0;
    }

    private static void ReplaceRows(ObservableCollection<UsageRowViewModel> target, IEnumerable<UsageRowViewModel> rows)
    {
        var list = rows.ToList();
        if (target.SequenceEqual(list))
        {
            return;
        }

        target.Clear();
        foreach (var row in list)
        {
            target.Add(row);
        }
    }

    private void SyncSecondary(List<UsageWindow> windows, DateTimeOffset now)
    {
        for (var i = SecondaryWindows.Count - 1; i >= 0; i--)
        {
            if (!windows.Any(w => string.Equals(w.Key, SecondaryWindows[i].Key, StringComparison.OrdinalIgnoreCase)))
            {
                SecondaryWindows.RemoveAt(i);
            }
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var existing = SecondaryWindows.FirstOrDefault(v => string.Equals(v.Key, window.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                SecondaryWindows.Insert(Math.Min(i, SecondaryWindows.Count), new UsageWindowViewModel(window, now));
            }
            else
            {
                existing.Update(window, now);
            }
        }
    }

    private static string DescribeOverageAmount(OverageInfo overage)
    {
        var currency = overage.Currency ?? string.Empty;
        var used = overage.UsedAmount?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
        var limit = overage.LimitAmount?.ToString("0.##", CultureInfo.InvariantCulture);
        return (limit is null ? $"{used} {currency} spent" : $"{used} of {limit} {currency}").Trim();
    }

    private void UpdateHeader()
    {
        PlanTierText = FormatPlan(_status.Snapshot?.PlanTier ?? _account.OrganizationRateLimitTier);
        if (AccountHidden)
        {
            HasEmail = false;
            AccountText = string.Empty;
            HasAccount = false;
            return;
        }

        HasEmail = _account.Email is not null;
        AccountText = _account.Email is { } email
            ? IsEmailMasked ? AccountInfo.MaskEmail(email) : email
            : _account.DisplayName ?? string.Empty;
        HasAccount = AccountText.Length > 0;
    }

    private void ShowFeedback(string? text)
    {
        RefreshFeedback = text;
        HasRefreshFeedback = !string.IsNullOrEmpty(text);
    }

    private void ClearFeedback() => ShowFeedback(null);

    partial void OnIsEmailMaskedChanged(bool value)
    {
        UpdateHeader();
        if (!_applyingSettings && _settings.Current.MaskEmail != value)
        {
            _settings.Update(s => s with { MaskEmail = value });
        }
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private void Refresh()
    {
        _analytics.RequestScan();
        if (_poller.TryRequestRefresh(out var reason))
        {
            ShowFeedback("Refreshing…");
            return;
        }

        ShowFeedback(reason);
    }

    [RelayCommand]
    private void ToggleEmailMask() => IsEmailMasked = !IsEmailMasked;

    private void OnStatusChanged(object? sender, PollStatus status) => _dispatcher.BeginInvoke(() => Apply(status));

    private void OnAnalyticsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Rebuild);

    private void OnChartRangeChanged(object? sender, EventArgs e)
    {
        _chartGeneration++;
        RefreshCharts(force: true);
        if (!_applyingSettings && _settings.Current.ChartRangeHours != Charts.RangeHours)
        {
            _settings.Update(s => s with { ChartRangeHours = Charts.RangeHours });
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.BeginInvoke(() =>
    {
        ApplySettings(settings);
        Rebuild();
    });

    private void OnPricingChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Rebuild);
}
