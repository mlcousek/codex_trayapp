using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Notifications;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class SqliteNotificationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayAppTests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "history.db");

    private static UsageSnapshot Snapshot(double percent = 85) => UsageSnapshot.Empty(Now) with
    {
        AccountKey = "synthetic-account",
        AccountIdentityKey = "synthetic-identity",
        AccountSourceKey = "synthetic-source",
        Windows = [UsageWindow.Create("codex:primary", percent, Now.AddHours(2))],
    };

    private ThresholdNotifier Notifier() => new(new SqliteNotificationStore(DatabasePath));

    [Fact]
    public void Restart_preserves_crossings_and_reset_does_not_erase_durable_claims()
    {
        Notifier().Evaluate(Snapshot(), [80, 95], Now).Single().Threshold.ShouldBe(80);
        var restarted = Notifier();
        restarted.Evaluate(Snapshot(), [80, 95], Now).ShouldBeEmpty();
        restarted.Evaluate(Snapshot(97), [80, 95], Now).Single().Threshold.ShouldBe(95);
        restarted.Reset();
        Notifier().Evaluate(Snapshot(99), [80, 95], Now).ShouldBeEmpty();
    }

    [Fact]
    public void Highest_simultaneous_crossing_claims_both_and_new_period_and_account_are_independent()
    {
        Notifier().Evaluate(Snapshot(99), [80, 95], Now).Single().Threshold.ShouldBe(95);
        Notifier().Evaluate(Snapshot(), [80, 95], Now).ShouldBeEmpty();
        Scalar("SELECT COUNT(*) FROM notification_crossings").ShouldBe(2);
        Notifier().Evaluate(Snapshot() with { AccountKey = "synthetic-other" }, [80, 95], Now).Count.ShouldBe(1);
        var newPeriod = Snapshot() with { Windows = [UsageWindow.Create("codex:primary", 85, Now.AddHours(7))] };
        Notifier().Evaluate(newPeriod, [80, 95], Now).Count.ShouldBe(1);
        Notifier().Evaluate(Snapshot(), [80, 95], Now).ShouldBeEmpty();
    }

    [Fact]
    public void Disabled_or_stale_evaluations_do_not_create_or_mutate_the_ledger()
    {
        Notifier().Evaluate(Snapshot(99), [], Now).ShouldBeEmpty();
        Notifier().Evaluate(Snapshot(99) with { Source = UsageSource.Cache }, [80, 95], Now).ShouldBeEmpty();
        File.Exists(DatabasePath).ShouldBeFalse();
        Notifier().Evaluate(Snapshot(99), [80, 95], Now).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Independent_instances_claim_a_simultaneous_crossing_only_once()
    {
        var first = Notifier();
        var second = Notifier();
        var results = await Task.WhenAll(
            Task.Run(() => first.Evaluate(Snapshot(99), [80, 95], Now), TestContext.Current.CancellationToken),
            Task.Run(() => second.Evaluate(Snapshot(99), [80, 95], Now), TestContext.Current.CancellationToken));
        results.Sum(r => r.Count).ShouldBe(1);
        Scalar("SELECT COUNT(*) FROM notification_crossings").ShouldBe(2);
    }

    [Fact]
    public void A_failed_second_insert_rolls_back_all_crossings()
    {
        var store = new SqliteNotificationStore(DatabasePath);
        store.TryMarkCrossings("schema", "schema", Now.AddHours(2), [80], Now);
        Execute("DELETE FROM notification_crossings; CREATE TRIGGER reject95 BEFORE INSERT ON notification_crossings WHEN NEW.threshold = 95 BEGIN SELECT RAISE(ABORT, 'synthetic rejection'); END;");
        Should.Throw<SqliteException>(() => store.TryMarkCrossings("account", "window", Now.AddHours(2), [80, 95], Now));
        Scalar("SELECT COUNT(*) FROM notification_crossings").ShouldBe(0);
        Execute("DROP TRIGGER reject95;");
        store.TryMarkCrossings("account", "window", Now.AddHours(2), [80, 95], Now).ShouldBe(95);
    }

    [Fact]
    public void Pruning_is_bounded_and_does_not_remove_current_claims()
    {
        var store = new SqliteNotificationStore(DatabasePath);
        store.TryMarkCrossings("account", "window", Now.AddHours(2), [80], Now);
        Execute($"WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<600) INSERT INTO notification_crossings SELECT 'expired', CAST(x AS TEXT), {Now.AddSeconds(-1).UtcTicks}, 80 FROM n;");
        store.TryMarkCrossings("account", "window", Now.AddHours(2), [80], Now).ShouldBeNull();
        Scalar("SELECT COUNT(*) FROM notification_crossings").ShouldBe(600 + 1 - SqliteNotificationStore.PruneBatchSize);
    }

    [Fact]
    public void Capacity_suppresses_new_alerts_without_evicting_active_claims()
    {
        var store = new SqliteNotificationStore(DatabasePath);
        store.TryMarkCrossings("account", "window", Now.AddHours(2), [80], Now);
        Execute($"WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<{SqliteNotificationStore.MaximumClaims - 1}) INSERT INTO notification_crossings SELECT 'active', CAST(x AS TEXT), {Now.AddHours(2).UtcTicks}, 80 FROM n;");
        store.TryMarkCrossings("other", "window", Now.AddHours(2), [80], Now).ShouldBeNull();
        store.TryMarkCrossings("account", "window", Now.AddHours(2), [80], Now).ShouldBeNull();
        Scalar("SELECT COUNT(*) FROM notification_crossings").ShouldBe(SqliteNotificationStore.MaximumClaims);
    }

    [Fact]
    public void Ledger_contains_hashes_instead_of_account_or_window_identifiers()
    {
        Notifier().Evaluate(Snapshot(), [80, 95], Now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_key, window_key FROM notification_crossings";
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).Length.ShouldBe(64);
        reader.GetString(1).Length.ShouldBe(64);
        reader.GetString(0).ShouldNotContain("synthetic");
        reader.GetString(1).ShouldNotContain("codex");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        connection.Open();
        return connection;
    }

    private long Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
