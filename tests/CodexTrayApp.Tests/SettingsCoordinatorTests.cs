using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Cache;
using CodexTrayApp.Core.History;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.Startup;
using CodexTrayApp.Theming;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>
/// How a settings change reaches the rest of the app: retention (and the scanner's age cutoff) with an immediate prune,
/// and the pricing file, which reloads only when the path really changed. Real store, poller, recorder, scanner and
/// pricing provider over temp files; the history store is a substitute so pruning can be counted.
/// The theme is left on "follow Windows" throughout: applying a theme needs a running WPF application, and with no
/// override requested the coordinator never touches the theme manager, which is created without its constructor so
/// it does not subscribe to system events either. The poll interval push is not asserted, because the poller keeps
/// its options private to Core.
/// </summary>
public sealed class SettingsCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;
    private readonly UsagePoller _poller;
    private readonly HistoryOptions _historyOptions = new() { RetentionDays = 90 };
    private readonly IHistoryStore _history = Substitute.For<IHistoryStore>();
    private readonly JsonlScanner _scanner;
    private readonly PricingProvider _pricing;
    private readonly SettingsCoordinator _coordinator;

    public SettingsCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(BundledPricing, """{"effectiveDate":"2026-06-24","currency":"USD","models":[{"id":"codex-a","input":1,"output":2}]}""");
        File.WriteAllText(CustomPricing, """{"effectiveDate":"2026-09-01","currency":"USD","models":[{"id":"codex-a","input":3,"output":4},{"id":"codex-b","input":5,"output":6}]}""");

        var clock = new FixedClock(Now);
        _store = new SettingsStore(Path.Combine(_directory, "settings.json"), NullLogger<SettingsStore>.Instance);
        _store.Load();
        _poller = new UsagePoller(Substitute.For<IUsageProvider>(), Substitute.For<ISnapshotCache>(), new PollingOptions(), clock, NullLogger<UsagePoller>.Instance);
        var recorder = new HistoryRecorder(_history, _historyOptions, clock, NullLogger<HistoryRecorder>.Instance);
        _scanner = new JsonlScanner(Path.Combine(_directory, "projects"), Substitute.For<IAnalyticsStore>(), clock, NullLogger<JsonlScanner>.Instance, TimeSpan.FromDays(90));
        _pricing = new PricingProvider(BundledPricing, NullLogger<PricingProvider>.Instance);
        var theme = (ThemeManager)RuntimeHelpers.GetUninitializedObject(typeof(ThemeManager));

        _coordinator = new SettingsCoordinator(
            _store,
            _poller,
            _historyOptions,
            recorder,
            _scanner,
            _pricing,
            theme,
            Dispatcher.CurrentDispatcher,
            NullLogger<SettingsCoordinator>.Instance);
    }

    private string BundledPricing => Path.Combine(_directory, "pricing.json");

    private string CustomPricing => Path.Combine(_directory, "custom.json");

    public void Dispose()
    {
        _coordinator.Dispose();
        _poller.Dispose();
        _store.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Start_applies_the_saved_retention_without_pruning()
    {
        _store.Update(s => s with { HistoryRetentionDays = 30 });

        _coordinator.Start();

        _historyOptions.RetentionDays.ShouldBe(30);
        _scanner.MaxAge.ShouldBe(TimeSpan.FromDays(30));
        _history.DidNotReceive().Prune(Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public void A_retention_change_prunes_right_away()
    {
        _coordinator.Start();

        _store.Update(s => s with { HistoryRetentionDays = 7 });

        _historyOptions.RetentionDays.ShouldBe(7);
        _scanner.MaxAge.ShouldBe(TimeSpan.FromDays(7));
        _history.Received(1).Prune(Now.AddDays(-7));
    }

    [Fact]
    public void An_unrelated_change_neither_prunes_nor_touches_the_scanner()
    {
        _coordinator.Start();

        _store.Update(s => s with { MaskEmail = !s.MaskEmail });

        _history.DidNotReceive().Prune(Arg.Any<DateTimeOffset>());
        _scanner.MaxAge.ShouldBe(TimeSpan.FromDays(90));
    }

    [Fact]
    public void A_pricing_file_setting_loads_that_file()
    {
        _coordinator.Start();
        _pricing.Current.SourcePath.ShouldBe(BundledPricing);

        _store.Update(s => s with { PricingFilePath = CustomPricing });

        _pricing.OverridePath.ShouldBe(CustomPricing);
        _pricing.Current.SourcePath.ShouldBe(CustomPricing);
        _pricing.Current.Models.Count.ShouldBe(2);
    }

    [Fact]
    public void The_pricing_file_is_not_reloaded_when_its_path_did_not_change()
    {
        _coordinator.Start();
        _store.Update(s => s with { PricingFilePath = CustomPricing });

        // Were the table reloaded now, the missing file would send it back to the bundled one.
        File.Delete(CustomPricing);
        _store.Update(s => s with { MaskEmail = !s.MaskEmail });
        _store.Update(s => s with { PricingFilePath = CustomPricing.ToUpperInvariant() });

        _pricing.Current.SourcePath.ShouldBe(CustomPricing);
    }

    [Fact]
    public void Clearing_the_pricing_file_goes_back_to_the_bundled_table()
    {
        _coordinator.Start();
        _store.Update(s => s with { PricingFilePath = CustomPricing });

        _store.Update(s => s with { PricingFilePath = null });

        _pricing.OverridePath.ShouldBeNull();
        _pricing.Current.SourcePath.ShouldBe(BundledPricing);
    }

    [Fact]
    public void After_Dispose_changes_are_no_longer_followed()
    {
        _coordinator.Start();
        _coordinator.Dispose();

        _store.Update(s => s with { HistoryRetentionDays = 7 });

        _historyOptions.RetentionDays.ShouldBe(90);
        _history.DidNotReceive().Prune(Arg.Any<DateTimeOffset>());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
