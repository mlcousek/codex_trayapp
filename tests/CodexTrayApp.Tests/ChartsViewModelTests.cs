using System.Globalization;
using System.Windows.Media;
using CodexTrayApp.Charts;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.ViewModels;
using Shouldly;

namespace CodexTrayApp.Tests;

public class ChartsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 16, 0, 0, TimeSpan.Zero);
    private static readonly ChartPalette Palette = new(key => key == "DangerBrush" ? Brushes.Red : Brushes.Gray);

    [Fact]
    public void Apply_selects_the_first_chart_that_has_data()
    {
        var vm = new ChartsViewModel(Palette, TimeZoneInfo.Utc);
        var daily = ChartDataBuilder.Daily([new DailyModelTotals(new DateOnly(2026, 9, 7), "codex-fable-5-1", new TokenTotals(10, 0, 0, 0, 0, 1))], new DateOnly(2026, 9, 7));

        vm.Apply(new ChartBundle(ChartDataBuilder.History([], Now.AddDays(-1), Now), null, daily, new Dictionary<string, SparkData>()));

        vm.HasAnyData.ShouldBeTrue();
        vm.HasBlock.ShouldBeFalse();
        vm.HasHistory.ShouldBeFalse();
        vm.HasDaily.ShouldBeTrue();
        vm.SelectedChart.ShouldBe(ChartKind.Daily);
        vm.IsDailySelected.ShouldBeTrue();
        vm.BlockSummary.ShouldBe("No Current window block in progress.");
    }

    [Fact]
    public void Empty_bundle_hides_the_section()
    {
        var vm = new ChartsViewModel(Palette, TimeZoneInfo.Utc);

        vm.Apply(new ChartBundle(ChartDataBuilder.History([], Now.AddDays(-1), Now), null, ChartDataBuilder.Daily([], new DateOnly(2026, 9, 7)), new Dictionary<string, SparkData>()));

        vm.HasAnyData.ShouldBeFalse();
        vm.SelectedChart.ShouldBe(ChartKind.Block);
    }

    [Fact]
    public void Block_chart_carries_the_projection_and_marks_the_limit()
    {
        var vm = new ChartsViewModel(Palette, TimeZoneInfo.Utc);
        var start = Now.AddHours(-2);
        var end = Now.AddHours(3);
        var block = new BlockAnalytics(start, end, TimeSpan.FromHours(2), TokenTotals.Zero, null, 0, 30, Now.AddHours(2), true);
        var chart = ChartDataBuilder.Burn([new HistoryPoint(start, 0, null), new HistoryPoint(Now.AddHours(-1), 20, null)], block, 40, Now, TimeZoneInfo.Utc);

        vm.ApplyBlock(chart);

        vm.HasBlock.ShouldBeTrue();
        vm.BlockSeries.Select(s => s.Name).ShouldBe(["Current window", "projected"]);
        vm.BlockSeries[1].Dashed.ShouldBeTrue();
        vm.BlockMarkers.Count.ShouldBe(1);
        vm.BlockMarkers[0].Label.ShouldBe("limit " + Clock(Now.AddHours(2)));
        vm.BlockMarkers[0].Stroke.ShouldBeSameAs(Brushes.Red);
        vm.BlockMinX.ShouldBe(ChartsViewModel.ToX(start));
        vm.BlockMaxX.ShouldBe(ChartsViewModel.ToX(end));
        vm.BlockStartLabel.ShouldBe(Clock(start));
        vm.BlockEndLabel.ShouldBe("reset " + Clock(end));
        vm.BlockTimeLabeler(ChartsViewModel.ToX(Now)).ShouldBe(Clock(Now));
    }

    [Fact]
    public void Daily_series_split_by_model_only_when_asked()
    {
        var vm = new ChartsViewModel(Palette, TimeZoneInfo.Utc);
        var today = new DateOnly(2026, 9, 7);
        var daily = ChartDataBuilder.Daily(
            [
                new DailyModelTotals(today, "codex-fable-5-1", new TokenTotals(300, 0, 0, 0, 0, 1)),
                new DailyModelTotals(today, "codex-sonnet-4-5-20250929", new TokenTotals(100, 0, 0, 0, 0, 1)),
            ],
            today);

        vm.ApplyDaily(daily);
        vm.DailySeries.Select(s => s.Name).ShouldBe(["tokens"]);
        vm.DailySeries[0].Values[^1].ShouldBe(400);

        vm.StackByModel = true;
        vm.DailySeries.Select(s => s.Name).ShouldBe(["fable-5-1", "sonnet-4-5"]);
        vm.DailyLabels.Count.ShouldBe(14);
        vm.TokenFormatter(1_234_567).ShouldBe("1.2M");
    }

    [Fact]
    public void Changing_the_range_asks_for_a_reload()
    {
        var vm = new ChartsViewModel(Palette, TimeZoneInfo.Utc);
        var raised = 0;
        vm.RangeChanged += (_, _) => raised++;

        vm.IsWeekRange = true;
        vm.IsWeekRange = false;

        vm.RangeHours.ShouldBe(ChartsViewModel.WeekRange);
        vm.IsDayRange.ShouldBeFalse();
        ChartsViewModel.RangeLabel(ChartsViewModel.DayRange).ShouldBe("24 h");
        ChartsViewModel.RangeLabel(ChartsViewModel.WeekRange).ShouldBe("7 d");
        ChartsViewModel.RangeLabel(ChartsViewModel.MonthRange).ShouldBe("30 d");
        raised.ShouldBe(1);
        vm.HistoryTimeLabeler(ChartsViewModel.ToX(Now)).ShouldBe(Now.ToString("MMM d HH:mm", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void Selecting_a_chart_ignores_unchecks()
    {
        var vm = new ChartsViewModel(Palette, TimeZoneInfo.Utc);

        vm.IsHistorySelected = true;
        vm.IsBlockSelected = false;

        vm.SelectedChart.ShouldBe(ChartKind.History);
    }

    private static string Clock(DateTimeOffset at) => at.ToString("t", CultureInfo.CurrentCulture);
}
