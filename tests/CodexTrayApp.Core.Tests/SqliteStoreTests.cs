using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Storage;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class SqliteStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;

    public SqliteStoreTests()
    {
        _store = new SqliteStore(Path.Combine(_root, "nested", "history.db"));
    }

    private static UsageEvent Event(string id, DateTimeOffset at, string model = "codex-opus-5", string project = "p", long input = 100, long output = 10) =>
        new(id, "req", at, model, project, "s", input, output, 5, 6, 7);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Inserts_ignore_duplicates_and_totals_group_by_model()
    {
        _store.InsertEvents([Event("a", Now.AddHours(-1)), Event("a", Now.AddHours(-1)), Event("b", Now.AddHours(-2), "codex-sonnet-5", "q", 1, 1)]).ShouldBe(2);

        _store.CountEvents().ShouldBe(2);
        var totals = _store.TotalsByModel(Now.AddDays(-1), Now);
        totals.Count.ShouldBe(2);
        totals[0].Model.ShouldBe("codex-opus-5");
        totals[0].Tokens.ShouldBe(new TokenTotals(100, 10, 5, 6, 7, 1));
        totals[0].Tokens.Total.ShouldBe(128);
    }

    [Fact]
    public void Range_queries_are_half_open()
    {
        _store.InsertEvents([Event("a", Now.AddHours(-1)), Event("b", Now)]);

        _store.TotalsByModel(Now.AddHours(-1), Now).Sum(t => t.Tokens.Messages).ShouldBe(1);
    }

    [Fact]
    public void Top_projects_rank_by_tokens()
    {
        _store.InsertEvents([Event("a", Now.AddHours(-1), project: "small", input: 1), Event("b", Now.AddHours(-1), project: "big", input: 9000), Event("c", Now.AddHours(-1), project: "big", input: 9000)]);

        var projects = _store.TopProjects(Now.AddDays(-1), Now, 5);

        projects[0].Project.ShouldBe("big");
        projects[0].Tokens.ShouldBe(2 * (9000 + 10 + 5 + 6 + 7));
        projects[1].Project.ShouldBe("small");
    }

    [Fact]
    public void Daily_totals_use_the_local_day_boundary()
    {
        // 23:30 UTC on the 6th is 01:30 on the 7th at UTC+2.
        _store.InsertEvents([Event("a", new DateTimeOffset(2026, 9, 6, 23, 30, 0, TimeSpan.Zero)), Event("b", new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero))]);

        var utc = _store.DailyTotals(Now.AddDays(-2), Now, TimeSpan.Zero);
        var plusTwo = _store.DailyTotals(Now.AddDays(-2), Now, TimeSpan.FromHours(2));

        utc.Select(d => d.Day).Distinct().Count().ShouldBe(2);
        plusTwo.ShouldHaveSingleItem().Day.ShouldBe(new DateOnly(2026, 9, 7));
        plusTwo[0].Tokens.Messages.ShouldBe(2);
    }

    [Fact]
    public void History_round_trips_per_window_and_prunes()
    {
        var snapshot = new UsageSnapshot(
            [UsageWindow.Create("codex:primary", 51, Now.AddHours(2)), UsageWindow.Create("codex:secondary", 9, null)],
            "max", null, null, Now, UsageSource.Live);
        var old = snapshot with { LastUpdated = Now.AddDays(-100), Windows = [UsageWindow.Create("codex:primary", 1, null)] };

        _store.AppendSnapshot(old);
        _store.AppendSnapshot(snapshot);
        _store.AppendSnapshot(snapshot);

        _store.GetWindowKeys().ShouldBe(["codex:primary", "codex:secondary"]);
        var series = _store.GetSeries("codex:primary", Now.AddDays(-365), Now.AddSeconds(1));
        series.Count.ShouldBe(2);
        series[1].Percent.ShouldBe(51);
        series[1].ResetsAt.ShouldBe(Now.AddHours(2));

        _store.Prune(Now.AddDays(-90)).ShouldBe(1);
        _store.GetSeries("codex:primary", Now.AddDays(-365), Now.AddSeconds(1)).Count.ShouldBe(1);
    }

    [Fact]
    public void Clear_removes_history_but_preserves_ingestion_cursors_and_schema()
    {
        _store.InsertEvents([Event("a", Now)]);
        _store.SetScanState(new ScanState("x", 1, 2, 3));

        _store.Clear();

        _store.CountEvents().ShouldBe(0);
        _store.GetScanState("x").ShouldBe(new ScanState("x", 1, 2, 3));
        _store.InsertEvents([Event("b", Now)]).ShouldBe(1);
    }

    [Fact]
    public void Scan_state_upserts()
    {
        _store.SetScanState(new ScanState("f", 10, 20, 30));
        _store.SetScanState(new ScanState("f", 15, 25, 35));

        _store.GetScanState("f").ShouldBe(new ScanState("f", 15, 25, 35));
    }
}
