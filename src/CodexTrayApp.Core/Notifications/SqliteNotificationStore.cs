using Microsoft.Data.Sqlite;

namespace CodexTrayApp.Core.Notifications;

/// <summary>A separate ledger in the app database, containing only hashed identity keys and numerical crossings.</summary>
public sealed class SqliteNotificationStore : INotificationStore
{
    internal const int MaximumClaims = 10000;
    internal const int PruneBatchSize = 512;
    private readonly string _connectionString;

    public SqliteNotificationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public int? TryMarkCrossings(string accountFingerprint, string windowFingerprint, DateTimeOffset reset,
        IReadOnlyCollection<int> thresholds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (thresholds.Count == 0 || reset <= now)
        {
            return null;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS notification_crossings (
                account_key TEXT NOT NULL, window_key TEXT NOT NULL, reset_ticks INTEGER NOT NULL,
                threshold INTEGER NOT NULL, PRIMARY KEY (account_key, window_key, reset_ticks, threshold));
            CREATE INDEX IF NOT EXISTS ix_notification_crossings_reset ON notification_crossings(reset_ticks);
            DELETE FROM notification_crossings WHERE rowid IN (
                SELECT rowid FROM notification_crossings WHERE reset_ticks <= $now ORDER BY reset_ticks LIMIT $batch);
            """;
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.Parameters.AddWithValue("$batch", PruneBatchSize);
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$account", accountFingerprint);
        command.Parameters.AddWithValue("$window", windowFingerprint);
        command.Parameters.AddWithValue("$reset", reset.UtcTicks);
        command.CommandText = "SELECT COALESCE(MAX(threshold), 0) FROM notification_crossings WHERE account_key = $account AND window_key = $window AND reset_ticks = $reset";
        var priorHighest = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "SELECT COUNT(*) FROM notification_crossings";
        var count = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var crossed = thresholds.Distinct().ToArray();
        // Never evict an active claim to make room: that would allow a duplicate on the next poll.
        if (count + crossed.Length > MaximumClaims)
        {
            transaction.Commit();
            return null;
        }

        command.CommandText = "INSERT OR IGNORE INTO notification_crossings (account_key, window_key, reset_ticks, threshold) VALUES ($account, $window, $reset, $threshold)";
        var parameter = command.Parameters.Add("$threshold", SqliteType.Integer);
        foreach (var threshold in crossed)
        {
            parameter.Value = threshold;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        var highest = crossed.Max();
        return highest > priorHighest ? highest : null;
    }
}
