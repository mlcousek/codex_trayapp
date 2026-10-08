using System.Globalization;
using CodexTrayApp.Charts;
using CodexTrayApp.Controls;
using CodexTrayApp.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexTrayApp.ViewModels;

public enum ChartKind
{
    Block,
    History,
    Daily,
}

/// <summary>
/// The charts section of the flyout: one chart at a time (this block, history, daily), the history range, and the
/// daily by-model toggle. Data arrives through <see cref="Apply"/>; this class only turns it into brushes and points.
/// </summary>
public sealed partial class ChartsViewModel : ObservableObject
{
    public const int DayRange = 24;
    public const int WeekRange = 24 * 7;
    public const int MonthRange = 24 * 30;

    private readonly ChartPalette _palette;
    private readonly TimeZoneInfo _zone;
    private DailyChart? _daily;
    private bool _autoSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlockSelected), nameof(IsHistorySelected), nameof(IsDailySelected))]
    private ChartKind _selectedChart = ChartKind.Block;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayRange), nameof(IsWeekRange), nameof(IsMonthRange))]
    private int _rangeHours = DayRange;

    [ObservableProperty]
    private bool _stackByModel;

    [ObservableProperty]
    private bool _hasAnyData;

    [ObservableProperty]
    private bool _hasBlock;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _blockSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartMarker> _blockMarkers = [];

    [ObservableProperty]
    private double _blockMinX;

    [ObservableProperty]
    private double _blockMaxX = 1;

    [ObservableProperty]
    private string _blockStartLabel = string.Empty;

    [ObservableProperty]
    private string _blockEndLabel = string.Empty;

    [ObservableProperty]
    private string _blockSummary = "No readings for this block yet.";

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _historySeries = [];

    [ObservableProperty]
    private double _historyMinX;

    [ObservableProperty]
    private double _historyMaxX = 1;

    [ObservableProperty]
    private string _historyStartLabel = string.Empty;

    [ObservableProperty]
    private string _historyEndLabel = string.Empty;

    [ObservableProperty]
    private string _historySummary = "No history recorded yet.";

    [ObservableProperty]
    private bool _hasDaily;

    /// <summary>False when the user hides local analytics: the daily token chart is one of them.</summary>
    [ObservableProperty]
    private bool _showDaily = true;

    [ObservableProperty]
    private IReadOnlyList<BarSeries> _dailySeries = [];

    [ObservableProperty]
    private IReadOnlyList<string> _dailyLabels = [];

    [ObservableProperty]
    private string _dailySummary = "No tokens recorded yet.";

    public ChartsViewModel(ChartPalette palette, TimeZoneInfo? zone = null)
    {
        _palette = palette;
        _zone = zone ?? TimeZoneInfo.Local;
        BlockTimeLabeler = x => Local(x).ToString("t", CultureInfo.CurrentCulture);
        HistoryTimeLabeler = x => Local(x).ToString(RangeHours > DayRange ? "MMM d HH:mm" : "t", CultureInfo.CurrentCulture);
    }

    /// <summary>Raised when the history range changes and the data has to be loaded again.</summary>
    public event EventHandler? RangeChanged;

    public Func<double, string> BlockTimeLabeler { get; }

    public Func<double, string> HistoryTimeLabeler { get; }

    public Func<double, string> TokenFormatter { get; } = value => TokenFormat.Compact(value);

    public bool IsBlockSelected
    {
        get => SelectedChart == ChartKind.Block;
        set => Select(value, ChartKind.Block);
    }

    public bool IsHistorySelected
    {
        get => SelectedChart == ChartKind.History;
        set => Select(value, ChartKind.History);
    }

    public bool IsDailySelected
    {
        get => SelectedChart == ChartKind.Daily;
        set => Select(value, ChartKind.Daily);
    }

    public bool IsDayRange
    {
        get => RangeHours == DayRange;
        set => SetRange(value, DayRange);
    }

    public bool IsWeekRange
    {
        get => RangeHours == WeekRange;
        set => SetRange(value, WeekRange);
    }

    public bool IsMonthRange
    {
        get => RangeHours == MonthRange;
        set => SetRange(value, MonthRange);
    }

    /// <summary>"24 h", "7 d" or "30 d"; other values fall back to hours.</summary>
    public static string RangeLabel(int hours) => hours switch
    {
        WeekRange => "7 d",
        MonthRange => "30 d",
        _ => hours.ToString(CultureInfo.CurrentCulture) + " h",
    };

    /// <summary>Chart X coordinates are Unix milliseconds: exact in a double and cheap to label.</summary>
    public static double ToX(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    /// <summary>Projects a bundle onto the bindable state. Must run on the UI thread because it hands out theme brushes.</summary>
    public void Apply(ChartBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ApplyBlock(bundle.Block);
        ApplyHistory(bundle.History);
        ApplyDaily(bundle.Daily);
        HasAnyData = HasBlock || HasHistory || HasDaily;

        if (!_autoSelected && HasAnyData)
        {
            _autoSelected = true;
            if (!HasSelectedData())
            {
                SelectedChart = HasBlock ? ChartKind.Block : HasHistory ? ChartKind.History : ChartKind.Daily;
            }
        }
    }

    internal void ApplyBlock(BurnChart? chart)
    {
        if (chart is null)
        {
            HasBlock = false;
            BlockSeries = [];
            BlockMarkers = [];
            BlockSummary = "No Current window block in progress.";
            return;
        }

        var series = new List<ChartSeries> { new("Current window", _palette.Series(0), ToChartPoints(chart.Recorded)) };
        if (chart.Projection.Count >= 2)
        {
            series.Add(new ChartSeries("projected", _palette.Secondary, ToChartPoints(chart.Projection), Dashed: true));
        }

        BlockSeries = series;
        BlockMarkers = chart.LimitAt is { } limit
            ? [new ChartMarker(ToX(limit), "limit " + Local(ToX(limit)).ToString("t", CultureInfo.CurrentCulture), _palette.Danger, Dashed: true)]
            : [];
        BlockMinX = ToX(chart.From);
        BlockMaxX = ToX(chart.To);
        BlockStartLabel = Local(BlockMinX).ToString("t", CultureInfo.CurrentCulture);
        BlockEndLabel = "reset " + Local(BlockMaxX).ToString("t", CultureInfo.CurrentCulture);
        BlockSummary = chart.Summary;
        HasBlock = chart.HasData;
    }

    internal void ApplyHistory(HistoryChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        HistorySeries = chart.Series
            .Select((s, i) => new ChartSeries(s.Name, _palette.Series(i), ToChartPoints(s.Points)))
            .ToList();
        HistoryMinX = ToX(chart.From);
        HistoryMaxX = ToX(chart.To);
        HistoryStartLabel = RangeLabel(RangeHours) + " ago";
        HistoryEndLabel = "now";
        HistorySummary = chart.Summary;
        HasHistory = chart.HasData;
    }

    internal void ApplyDaily(DailyChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _daily = chart;
        DailyLabels = chart.Labels;
        DailySummary = chart.Summary;
        HasDaily = chart.HasData && ShowDaily;
        RebuildDailySeries();
    }

    partial void OnShowDailyChanged(bool value)
    {
        HasDaily = value && _daily is { HasData: true };
        HasAnyData = HasBlock || HasHistory || HasDaily;
        if (!value && SelectedChart == ChartKind.Daily)
        {
            SelectedChart = HasBlock || !HasHistory ? ChartKind.Block : ChartKind.History;
        }
    }

    partial void OnRangeHoursChanged(int value) => RangeChanged?.Invoke(this, EventArgs.Empty);

    partial void OnStackByModelChanged(bool value) => RebuildDailySeries();

    private static List<ChartPoint> ToChartPoints(IReadOnlyList<TimePoint> points) =>
        points.Select(p => new ChartPoint(ToX(p.At), p.Value) { BreakBefore = p.BreakBefore }).ToList();

    private DateTimeOffset Local(double x) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)x), _zone);

    private void RebuildDailySeries()
    {
        if (_daily is not { } daily)
        {
            DailySeries = [];
            return;
        }

        if (StackByModel && daily.ByModel.Count > 0)
        {
            DailySeries = daily.ByModel
                .Select((m, i) => m.Model == ChartDataBuilder.OtherModels
                    ? new BarSeries(m.Model, _palette.Series(ChartPalette.SeriesCount - 1), m.Values)
                    : new BarSeries(ChartDataBuilder.ShortModelName(m.Model), _palette.Series(i), m.Values))
                .ToList();
            return;
        }

        DailySeries = [new BarSeries("tokens", _palette.Series(0), daily.Totals)];
    }

    private bool HasSelectedData() => SelectedChart switch
    {
        ChartKind.Block => HasBlock,
        ChartKind.History => HasHistory,
        _ => HasDaily,
    };

    private void Select(bool value, ChartKind kind)
    {
        if (value)
        {
            SelectedChart = kind;
        }
    }

    private void SetRange(bool value, int hours)
    {
        if (value)
        {
            RangeHours = hours;
        }
    }
}
