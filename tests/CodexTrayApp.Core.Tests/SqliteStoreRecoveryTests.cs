using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

/// <summary>
/// What happens when history.db is damaged, as it was on 2026-09-10 ("database disk image is malformed" on every
/// scan and chart load until the file was replaced). Each test damages a real SQLite file the way corruption does:
/// a table's root page overwritten, or a file that was never a database.
/// </summary>
public sealed class SqliteStoreRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    public SqliteStoreRecoveryTests() => Directory.CreateDirectory(_root);

    private string DatabasePath => Path.Combine(_root, "history.db");

    private string[] SetAside => Directory.GetFiles(_root, "history.corrupt-*.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_healthy_database_is_left_alone()
    {
        Seed();

        var store = new SqliteStore(DatabasePath);

        store.CountEvents().ShouldBe(2);
        store.GetSeries("codex:primary", Now.AddDays(-1), Now).Count.ShouldBe(2);
        store.RecoveredFrom.ShouldBeNull();
        SetAside.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_database_is_simply_created()
    {
        var store = new SqliteStore(DatabasePath);

        store.CountEvents().ShouldBe(0);
        store.RecoveredFrom.ShouldBeNull();
        File.Exists(DatabasePath).ShouldBeTrue();
    }

    [Fact]
    public void A_damaged_events_table_is_set_aside_and_the_readable_history_kept()
    {
        Seed();
        Damage("usage_events");
        var log = new ListLogger<SqliteStore>();

        var store = new SqliteStore(DatabasePath, log);

        store.GetSeries("codex:primary", Now.AddDays(-1), Now).Select(p => p.Percent).ShouldBe([10.0, 20.0]);
        store.GetSeries("codex:secondary", Now.AddDays(-1), Now).Count.ShouldBe(2);
        store.CountEvents().ShouldBe(0);
        store.GetScanState("session.jsonl").ShouldBeNull();
        store.SalvagedRows.ShouldBe(4);
        store.RecoveredFrom.ShouldNotBeNull();
        SetAside.ShouldBe([store.RecoveredFrom!]);
        SqliteStore.FindDamage(DatabasePath).ShouldBeNull();
        log.All.ShouldContain("history.db was damaged");
    }

    [Fact]
    public void A_damaged_history_table_still_keeps_the_readable_usage_events()
    {
        Seed();
        Damage("snapshot_history");

        var store = new SqliteStore(DatabasePath);

        store.CountEvents().ShouldBe(2);
        store.GetSeries("codex:primary", Now.AddDays(-1), Now).ShouldBeEmpty();
        store.SalvagedRows.ShouldBe(2);
    }

    [Fact]
    public void The_rebuilt_database_accepts_new_rows()
    {
        Seed();
        Damage("usage_events");
        var store = new SqliteStore(DatabasePath);

        store.InsertEvents([Event("c", Now)]).ShouldBe(1);
        store.AppendSnapshot(Snapshot(Now, 30));

        store.CountEvents().ShouldBe(1);
        store.GetSeries("codex:primary", Now.AddDays(-1), Now.AddMinutes(1)).Select(p => p.Percent).ShouldBe([10.0, 20.0, 30.0]);
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_replaced_and_kept()
    {
        File.WriteAllText(DatabasePath, new string('x', 4096));

        var store = new SqliteStore(DatabasePath);

        store.CountEvents().ShouldBe(0);
        store.SalvagedRows.ShouldBe(0);
        File.ReadAllText(store.RecoveredFrom.ShouldNotBeNull()).ShouldBe(new string('x', 4096));
    }

    [Fact]
    public void With_recovery_off_a_damaged_database_stays_where_it_is()
    {
        Seed();
        Damage("usage_events");

        var store = new SqliteStore(DatabasePath, recoverCorruption: false);

        // COUNT(*) is answered from an intact index, so read the table rows themselves.
        Should.Throw<SqliteException>(() => store.TotalsByModel(Now.AddDays(-1), Now.AddDays(1)));
        store.RecoveredFrom.ShouldBeNull();
        SetAside.ShouldBeEmpty();
    }

    [Fact]
    public void A_second_rebuild_the_same_second_does_not_overwrite_the_first()
    {
        var at = new DateTime(2026, 9, 10, 6, 49, 35, DateTimeKind.Local);
        var first = SqliteStore.AsidePath(DatabasePath, at);
        File.WriteAllText(first, "earlier");

        var second = SqliteStore.AsidePath(DatabasePath, at);

        Path.GetFileName(first).ShouldBe("history.corrupt-20260910-064935.db");
        Path.GetFileName(second).ShouldBe("history.corrupt-20260910-064935-2.db");
    }

    private static UsageSnapshot Snapshot(DateTimeOffset at, double percent) => new(
        [UsageWindow.Create("codex:primary", percent, at.AddHours(2)), UsageWindow.Create("codex:secondary", 40, at.AddDays(3))],
        "max",
        null,
        null,
        at,
        UsageSource.Live);

    private static UsageEvent Event(string id, DateTimeOffset at) => new(id, "req", at, "codex-opus-5", "p", "s", 100, 10, 5, 6, 7);

    /// <summary>Two snapshots of two windows, two events and a scan offset, all flushed into the main file.</summary>
    private void Seed()
    {
        var store = new SqliteStore(DatabasePath);
        store.AppendSnapshot(Snapshot(Now.AddHours(-2), 10));
        store.AppendSnapshot(Snapshot(Now.AddHours(-1), 20));
        store.InsertEvents([Event("a", Now.AddHours(-1)), Event("b", Now.AddMinutes(-5))]);
        store.SetScanState(new ScanState("session.jsonl", 10, 10, 1));

        // Closing the last connection checkpoints the write-ahead log into the main file, where the damage goes.
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Overwrites a table's root page with bytes that are no valid page type, as a torn or foreign write would.</summary>
    private void Damage(string table)
    {
        long root;
        long pageSize;
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = $name";
            command.Parameters.AddWithValue("$name", table);
            root = (long)command.ExecuteScalar()!;
            command.CommandText = "PRAGMA page_size";
            pageSize = (long)command.ExecuteScalar()!;
        }

        SqliteConnection.ClearAllPools();
        using var file = new FileStream(DatabasePath, FileMode.Open, FileAccess.Write);
        file.Position = (root - 1) * pageSize;
        file.Write(Enumerable.Repeat((byte)0xFF, (int)pageSize).ToArray());
    }
}
