using System.IO;
using System.Windows.Threading;
using CodexTrayApp.Charts;
using CodexTrayApp.Core.Account;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Cache;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Tests;

public sealed class FlyoutAccountIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Account_switch_clears_quota_charts_and_sparks_but_preserves_local_daily_totals() => StaThread.Run(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Apply(Status("account-a"));
        vm.Charts.ShowDaily = true;
        vm.TryApplyCharts(Bundle(), vm.ChartGeneration, "account-a").ShouldBeTrue();
        vm.Charts.HasHistory.ShouldBeTrue();
        vm.Charts.HasBlock.ShouldBeTrue();
        vm.Primary!.HasSpark.ShouldBeTrue();
        vm.Apply(Status("account-b"));
        vm.Charts.HasHistory.ShouldBeFalse();
        vm.Charts.HistorySeries.ShouldBeEmpty();
        vm.Charts.HasBlock.ShouldBeFalse();
        vm.Primary!.HasSpark.ShouldBeFalse();
        vm.Charts.HasDaily.ShouldBeTrue();
        vm.Charts.DailySeries.Single().Values.ShouldBe([123d]);
        return true;
    });

    [Fact]
    public void Invalidated_account_rejects_old_bundle_even_when_same_account_returns() => StaThread.Run(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Apply(Status("account-a"));
        var generation = vm.ChartGeneration;
        vm.Apply(PollStatus.Initial(null));
        vm.HasAccount.ShouldBeFalse();
        vm.Charts.HasHistory.ShouldBeFalse();
        vm.TryApplyCharts(Bundle(), generation, "account-a").ShouldBeFalse();
        vm.Apply(Status("account-a"));
        vm.TryApplyCharts(Bundle(), generation, "account-a").ShouldBeFalse();
        vm.Charts.HasHistory.ShouldBeFalse();
        vm.Primary!.HasSpark.ShouldBeFalse();
        return true;
    });

    [Fact]
    public void New_status_rejects_previous_load_and_current_generation_accepts_new_bundle() => StaThread.Run(() =>
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Apply(Status("account-a"));
        var generation = vm.ChartGeneration;
        vm.Apply(Status("account-a") with { State = PollState.Stale });
        vm.TryApplyCharts(Bundle(), generation, "account-a").ShouldBeFalse();
        vm.TryApplyCharts(Bundle(), vm.ChartGeneration, "account-b").ShouldBeFalse();
        vm.TryApplyCharts(Bundle(), vm.ChartGeneration, "account-a").ShouldBeTrue();
        fixture.ViewModel.Dispose();
        vm.TryApplyCharts(Bundle(), vm.ChartGeneration, "account-a").ShouldBeFalse();
        return true;
    });

    [Fact]
    public void Chart_loader_uses_captured_account_for_all_quota_queries()
    {
        var store = Substitute.For<IHistoryStore>();
        store.GetWindowKeys("account-a").Returns(["codex:primary"]);
        store.GetSeries("codex:primary", Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), "account-a")
            .Returns([new HistoryPoint(Now.AddMinutes(-20), 10, Now.AddHours(1)), new HistoryPoint(Now, 20, Now.AddHours(1))]);
        var analytics = Substitute.For<IAnalyticsStore>();
        var calculator = new AnalyticsCalculator(analytics, () => PricingTable.Empty, TimeZoneInfo.Utc);
        var loader = new ChartDataLoader(store, calculator, TimeZoneInfo.Utc);
        var block = new BlockAnalytics(Now.AddHours(-4), Now.AddHours(1), TimeSpan.FromHours(4), TokenTotals.Zero, null, 0, null, null, false);
        var bundle = loader.Load(Status("account-a").Snapshot, block, 24, Now);
        bundle.History.HasData.ShouldBeTrue();
        store.Received(1).GetWindowKeys("account-a");
        store.Received(2).GetSeries("codex:primary", Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), "account-a");
        store.DidNotReceive().GetWindowKeys();
        store.DidNotReceive().GetSeries(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>());
    }

    private static PollStatus Status(string account) => new(PollState.Ok,
        new UsageSnapshot([UsageWindow.Create("codex:primary", 20, Now.AddHours(1))], "plus", account + "@example.test", null, Now, UsageSource.Live) { AccountKey = account },
        Now, Now, null, null, 0);

    private static ChartBundle Bundle()
    {
        TimePoint[] points = [new(Now.AddMinutes(-20), 10), new(Now, 20)];
        return new ChartBundle(
            new HistoryChart(Now.AddDays(-1), Now, [new TimeSeries("codex:primary", "Current window", points)], "Quota history"),
            new BurnChart(Now.AddHours(-4), Now.AddHours(1), points, [], null, false, "Quota block"),
            new DailyChart([DateOnly.FromDateTime(Now.DateTime)], ["today"], [123d], [], "Local daily tokens"),
            new Dictionary<string, SparkData> { ["codex:primary"] = new(Now.AddMinutes(-20), Now, points) });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SettingsStore _settings;
        private readonly UsagePoller _poller;
        private readonly LocalAnalyticsProvider _analytics;
        public FlyoutViewModel ViewModel { get; }
        public Fixture()
        {
            var path = Path.Combine(Path.GetTempPath(), "codex-flyout-test-" + Guid.NewGuid().ToString("N"));
            var store = Substitute.For<IAnalyticsStore>();
            var history = Substitute.For<IHistoryStore>();
            _settings = new SettingsStore(Path.Combine(path, "settings.json"), NullLogger<SettingsStore>.Instance);
            var pricing = new PricingProvider(Path.Combine(path, "pricing.json"), NullLogger<PricingProvider>.Instance);
            _poller = new UsagePoller(Substitute.For<IUsageProvider>(), Substitute.For<ISnapshotCache>(), new PollingOptions(), TimeProvider.System, NullLogger<UsagePoller>.Instance);
            _analytics = new LocalAnalyticsProvider(new JsonlScanner(path, store, TimeProvider.System, NullLogger<JsonlScanner>.Instance), path, NullLogger<LocalAnalyticsProvider>.Instance);
            ViewModel = new FlyoutViewModel(_poller, Substitute.For<IAccountInfoSource>(), _analytics, new AnalyticsCalculator(store, () => PricingTable.Empty), history,
                _settings, pricing, TimeProvider.System, Dispatcher.CurrentDispatcher, () => { }, NullLogger<FlyoutViewModel>.Instance);
        }
        public void Dispose()
        {
            ViewModel.Dispose();
            _analytics.Dispose();
            _poller.Dispose();
            _settings.Dispose();
        }
    }
}
