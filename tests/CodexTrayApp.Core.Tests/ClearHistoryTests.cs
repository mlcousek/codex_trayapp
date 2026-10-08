using System.Text.Json;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class ClearHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;

    public ClearHistoryTests() => _store = new SqliteStore(Path.Combine(_root, "history.db"));

    private JsonlScanner Scanner(IAnalyticsStore? store = null) => new(_root, store ?? _store, new FakeTimeProvider(Now), NullLogger<JsonlScanner>.Instance);

    private string Source(string name, string records, bool archived = false)
    {
        var folder = Path.Combine(_root, archived ? "archived_sessions" : "sessions");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".jsonl");
        File.WriteAllText(path, records);
        return path;
    }

    private static string Header(string session) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = session, cwd = "synthetic-project" } }) + "\n"
        + JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "gpt-5" } }) + "\n";

    private static string Tokens(long total, DateTimeOffset at) => JsonSerializer.Serialize(new
    {
        type = "event_msg", timestamp = at.ToString("O"),
        payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = total, output_tokens = 0, cached_input_tokens = 0, cache_write_input_tokens = 0 } } },
    }) + "\n";

    private long InputTotal() => _store.TotalsByModel(Now.AddDays(-1), Now.AddDays(1)).Sum(t => t.Tokens.Input);

    [Fact]
    public void Clear_baselines_unconsumed_and_unseen_sources_and_restart_ingests_only_new_deltas()
    {
        var first = Source("first", Header("first") + Tokens(100, Now.AddMinutes(-2)));
        var scanner = Scanner();
        scanner.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        File.AppendAllText(first, Tokens(150, Now.AddMinutes(-1)));
        var unseen = Source("unseen", Header("unseen") + Tokens(200, Now.AddMinutes(-1)), archived: true);
        var originals = new[] { File.ReadAllBytes(first), File.ReadAllBytes(unseen) };
        _store.AppendSnapshot(new UsageSnapshot([UsageWindow.Create("primary", 50, null)], null, null, null, Now.AddMinutes(-1), UsageSource.Live));

        scanner.ClearHistory(TestContext.Current.CancellationToken);

        _store.CountEvents().ShouldBe(0);
        _store.GetWindowKeys().ShouldBeEmpty();
        _store.GetScanState(first)!.Offset.ShouldBe(new FileInfo(first).Length);
        _store.GetScanState(unseen)!.Offset.ShouldBe(new FileInfo(unseen).Length);
        File.ReadAllBytes(first).ShouldBe(originals[0]);
        File.ReadAllBytes(unseen).ShouldBe(originals[1]);
        var restarted = Scanner(new SqliteStore(_store.DatabasePath));
        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
        File.AppendAllText(first, Tokens(180, Now.AddSeconds(1)));
        File.AppendAllText(unseen, Tokens(220, Now.AddSeconds(1)));
        Source("new", Header("new") + Tokens(10, Now.AddSeconds(1)));

        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(3);
        InputTotal().ShouldBe(60);
        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
    }

    [Fact]
    public void Preclear_partial_record_is_suppressed_on_completion_but_advances_the_counter_baseline()
    {
        var partial = Tokens(150, Now.AddSeconds(1));
        var split = partial.Length / 2;
        var path = Source("partial", Header("partial") + Tokens(100, Now.AddMinutes(-1)) + partial[..split]);
        Scanner().ClearHistory(TestContext.Current.CancellationToken);
        var restarted = Scanner(new SqliteStore(_store.DatabasePath));
        File.AppendAllText(path, partial[split..]);
        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
        File.AppendAllText(path, Tokens(180, Now.AddSeconds(2)));

        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        InputTotal().ShouldBe(30);
    }

    [Fact]
    public void Clear_ignores_retention_when_baselining_existing_sources()
    {
        var path = Source("old", Header("old") + Tokens(900, Now.AddDays(-200)));
        File.SetLastWriteTimeUtc(path, Now.AddDays(-200).UtcDateTime);
        var scanner = Scanner();
        scanner.MaxAge = TimeSpan.FromDays(90);
        scanner.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
        _store.GetScanState(path).ShouldBeNull();

        scanner.ClearHistory(TestContext.Current.CancellationToken);
        File.AppendAllText(path, Tokens(925, Now.AddSeconds(1)));

        scanner.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        InputTotal().ShouldBe(25);
    }

    [Fact]
    public void Truncation_and_renamed_historical_logs_cannot_restore_preclear_activity()
    {
        var path = Source("old", Header("old") + string.Concat(Enumerable.Range(1, 10).Select(i => Tokens(i * 100, Now.AddMinutes(-1)))));
        Scanner().ClearHistory(TestContext.Current.CancellationToken);
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, "renamed.jsonl");
        File.Move(path, renamed);
        File.WriteAllText(path, Header("replacement") + Tokens(50, Now.AddMinutes(-1)) + Tokens(90, Now.AddSeconds(1)));

        var restarted = Scanner();
        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        InputTotal().ShouldBe(40);
        File.AppendAllText(renamed, Tokens(1025, Now.AddSeconds(1)));
        restarted.Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        InputTotal().ShouldBe(65);
    }

    [Fact]
    public void Same_length_and_mtime_replacement_is_detected_by_source_fingerprint()
    {
        var path = Source("same", Header("old-session") + Tokens(100, Now.AddMinutes(-1)));
        Scanner().ClearHistory(TestContext.Current.CancellationToken);
        var mtime = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        File.WriteAllText(path, Header("new-session") + Tokens(100, Now.AddMinutes(-1)));
        File.SetLastWriteTimeUtc(path, mtime);
        new FileInfo(path).Length.ShouldBe(length);
        Scanner().Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
        File.AppendAllText(path, Tokens(125, Now.AddSeconds(1)));

        Scanner().Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        InputTotal().ShouldBe(25);
    }

    [Fact]
    public void Cancelled_clear_leaves_existing_history_and_cursors_intact()
    {
        var path = Source("cancel", Header("cancel") + Tokens(100, Now.AddMinutes(-1)));
        var scanner = Scanner();
        scanner.Scan(TestContext.Current.CancellationToken);
        var state = _store.GetScanState(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() => scanner.ClearHistory(cancellation.Token));

        _store.CountEvents().ShouldBe(1);
        _store.GetScanState(path).ShouldBe(state);
        _store.GetHistoryClearedAt().ShouldBeNull();
    }

    [Fact]
    public void Failed_baseline_transaction_rolls_back_deletions_and_metadata()
    {
        var path = Source("atomic", Header("atomic") + Tokens(100, Now.AddMinutes(-1)));
        Scanner().Scan(TestContext.Current.CancellationToken);
        var original = _store.GetScanState(path)!;
        var replacement = original with { Offset = 0 };

        Should.Throw<SqliteException>(() => _store.ClearAndBaseline([replacement, replacement], Now));

        _store.CountEvents().ShouldBe(1);
        _store.GetScanState(path).ShouldBe(original);
        _store.GetHistoryClearedAt().ShouldBeNull();
    }

    [Fact]
    public void Stale_inflight_account_samples_are_rejected_after_clear_and_fresh_samples_are_retained()
    {
        var snapshot = new UsageSnapshot([UsageWindow.Create("primary", 50, null)], null, null, null, Now.AddMinutes(-1), UsageSource.Live);
        _store.AppendSnapshot(snapshot);
        Scanner().ClearHistory(TestContext.Current.CancellationToken);

        _store.AppendSnapshot(snapshot);
        _store.AppendSnapshot(snapshot with { LastUpdated = Now });
        _store.GetWindowKeys().ShouldBeEmpty();
        _store.AppendSnapshot(snapshot with { LastUpdated = Now.AddSeconds(1) });
        _store.GetSeries("primary", Now.AddDays(-1), Now.AddDays(1)).ShouldHaveSingleItem().Timestamp.ShouldBe(Now.AddSeconds(1));
    }

    [Fact]
    public async Task Clear_waits_for_inflight_scan_and_removes_its_events_before_returning()
    {
        Source("concurrent", Header("concurrent") + Tokens(100, Now.AddMinutes(-1)));
        var intercept = Substitute.For<IAnalyticsStore>();
        intercept.GetScanState(Arg.Any<string>()).Returns(call => _store.GetScanState(call.Arg<string>()));
        intercept.When(s => s.SetScanState(Arg.Any<ScanState>())).Do(call => _store.SetScanState(call.Arg<ScanState>()));
        intercept.When(s => s.ClearAndBaseline(Arg.Any<IReadOnlyCollection<ScanState>>(), Arg.Any<DateTimeOffset>()))
            .Do(call => _store.ClearAndBaseline(call.Arg<IReadOnlyCollection<ScanState>>(), call.Arg<DateTimeOffset>()));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        intercept.InsertEvents(Arg.Any<IReadOnlyCollection<UsageEvent>>()).Returns(call =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            return _store.InsertEvents(call.Arg<IReadOnlyCollection<UsageEvent>>());
        });
        var scanner = Scanner(intercept);
        var scan = Task.Run(() => scanner.Scan(TestContext.Current.CancellationToken));
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        var clear = Task.Run(() => scanner.ClearHistory(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            clear.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.Set();
        }

        await scan;
        await clear;
        _store.CountEvents().ShouldBe(0);
        Scanner().Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
    }

    [Fact]
    public async Task Provider_clear_notifies_chart_subscribers_even_when_no_new_events_exist()
    {
        Source("provider", Header("provider") + Tokens(100, Now.AddMinutes(-1)));
        using var provider = new LocalAnalyticsProvider(Scanner(), _root, NullLogger<LocalAnalyticsProvider>.Instance);
        var changes = 0;
        provider.DataChanged += (_, _) => changes++;

        await provider.ClearHistoryAsync(TestContext.Current.CancellationToken);

        changes.ShouldBe(1);
        provider.LastScan.ShouldBeNull();
        _store.CountEvents().ShouldBe(0);
        Scanner().Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
