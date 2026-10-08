using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Storage;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class AnalyticsCalculatorLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;
    private readonly AnalyticsCalculator _calculator;

    public AnalyticsCalculatorLedgerTests()
    {
        _store = new SqliteStore(Path.Combine(_root, "history.db"));
        var pricing = new PricingTable(new DateOnly(2026, 10, 8), "USD",
            [new ModelPrice("priced", PriceMatch.Exact, 2, 10, 3, 4, 1)], null);
        _calculator = new AnalyticsCalculator(_store, () => pricing, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    private static UsageEvent Event(string id, DateTimeOffset timestamp, string model = "priced", string project = "p") =>
        new(id, "request", timestamp, model, project, "synthetic", 100, 20, 3, 4, 5);

    [Fact]
    public void Projection_uses_snapshot_account_even_when_active_account_changes()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(1);
        UsageSnapshot Snapshot(string? account, int ago, double percent) => new(
            [UsageWindow.Create(WindowKeys.FiveHour, percent, reset) with { DurationMinutes = 300 }],
            "test", null, null, now.AddMinutes(-ago), UsageSource.Live) { AccountKey = account };
        _store.AppendSnapshot(Snapshot("account-a", 10, 40));
        _store.AppendSnapshot(Snapshot("account-a", 5, 50));
        _store.AppendSnapshot(Snapshot("account-b", 10, 10));
        _store.AppendSnapshot(Snapshot("account-b", 5, 20));
        _store.ActiveAccountKey = "account-b";
        var a = _calculator.Compute(Snapshot("account-a", 0, 60), now).CurrentBlock!;
        a.PercentPerHour.ShouldBe(120);
        a.ProjectedLimitAt.ShouldBe(now.AddMinutes(20));
        _store.ActiveAccountKey = "account-a";
        var b = _calculator.Compute(Snapshot("account-b", 0, 30), now).CurrentBlock!;
        b.PercentPerHour.ShouldBe(120);
        b.ProjectedLimitAt.ShouldBe(now.AddMinutes(35));
        _calculator.Compute(Snapshot(null, 0, 60), now).CurrentBlock!.ProjectedLimitAt.ShouldBeNull();
        _store.GetWindowKeys("account-b").ShouldBe([WindowKeys.FiveHour]);
        _store.GetWindowKeys(null).ShouldBeEmpty();
    }

    [Fact]
    public void Today_uses_midnight_offset_before_spring_transition_and_half_open_end()
    {
        var start = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero);
        _store.InsertEvents([Event("before", start.AddTicks(-1)), Event("start", start),
            Event("after", start.AddMinutes(30)), Event("now", now)]);
        var result = _calculator.Compute(null, now);
        result.Today.From.ShouldBe(start);
        result.Today.Tokens.Messages.ShouldBe(2);
        result.Today.Tokens.Total.ShouldBe(264);
    }

    [Theory]
    [InlineData(3, 29, 23)]
    [InlineData(10, 25, 25)]
    public void Daily_chart_uses_actual_day_length_across_dst(int month, int day, int hours)
    {
        var localDay = new DateOnly(2026, month, day);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDay.ToDateTime(TimeOnly.MinValue), zone));
        var end = start.AddHours(hours);
        _store.InsertEvents([Event("prior", start.AddTicks(-1)), Event("first", start),
            Event("last", end.AddTicks(-1)), Event("next", end)]);
        var rows = _calculator.Daily(start.AddHours(-1), end.AddHours(1));
        rows.Single(r => r.Day == localDay).Tokens.Messages.ShouldBe(2);
        rows.Single(r => r.Day == localDay.AddDays(-1)).Tokens.Messages.ShouldBe(1);
        rows.Single(r => r.Day == localDay.AddDays(1)).Tokens.Messages.ShouldBe(1);
        rows.Sum(r => r.Tokens.Messages).ShouldBe(4);
    }

    [Fact]
    public void Ledger_preserves_model_project_totals_and_partial_cost()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        _store.InsertEvents([Event("known", now.AddHours(-1), project: "known-project"),
            Event("unknown", now.AddHours(-2), "unpriced", "unknown-project")]);
        var result = _calculator.Compute(null, now);
        result.Today.Tokens.Total.ShouldBe(264);
        result.Today.Cost.ShouldBe(0.000430m);
        result.Today.HasUnknownModels.ShouldBeTrue();
        result.Today.ByModel.Single(m => m.Model == "unpriced").Cost.ShouldBeNull();
        result.TopProjectsToday.Select(p => p.Project).ShouldBe(["known-project", "unknown-project"], ignoreOrder: true);
        result.EventCount.ShouldBe(2);
        result.CurrentBlock!.PercentPerHour.ShouldBeNull();
        result.CurrentBlock.ProjectedLimitAt.ShouldBeNull();
        result.CurrentBlock.TokensPerHour.ShouldBe(52.8);
    }
}
