using CodexTrayApp.Core.Storage;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class SqliteMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "history.db");

    [Fact]
    public void Failed_migration_rolls_back_schema_and_can_be_retried()
    {
        Directory.CreateDirectory(_root);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE notification_crossings (legacy_value TEXT); PRAGMA user_version = 0;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Should.Throw<SqliteException>(() => new SqliteStore(DatabasePath).CountEvents());
        SqliteConnection.ClearAllPools();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage_events'";
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
            command.CommandText = "DROP TABLE notification_crossings";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        new SqliteStore(DatabasePath).CountEvents().ShouldBe(0);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(2);
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'notification_crossings'";
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        }

        SqliteConnection.ClearAllPools();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE notification_crossings; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        new SqliteStore(DatabasePath).CountEvents().ShouldBe(0);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(2);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
