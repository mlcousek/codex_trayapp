using System.Globalization;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexTrayApp.Core.Storage;

/// <summary>
/// One SQLite file (<c>history.db</c>) for usage events, scan offsets and snapshot history.
/// Timestamps are stored as UTC ticks so range queries are plain integer comparisons.
/// The file is checked once per process before first use; a damaged one is set aside, never deleted, and replaced by
/// a fresh file holding every row that could still be read. Usage events are rebuilt from the session logs anyway;
/// the snapshot history cannot be, which is why it is worth salvaging.
/// </summary>
public sealed class SqliteStore : IAnalyticsStore, IHistoryStore
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const int CurrentSchemaVersion = 2;
    private const string HistoryColumns = "timestamp_ticks, window_key, percent, resets_at_ticks";
    private const string HistorySelect = "SELECT " + HistoryColumns + " FROM snapshot_history NOT INDEXED";
    private const string HistoryInsert = "INSERT OR IGNORE INTO snapshot_history (" + HistoryColumns + ") VALUES ($p0, $p1, $p2, $p3)";
    private const string EventColumns = "message_id, request_id, timestamp_ticks, model, project, session_id, input, output, cache_write_5m, cache_write_1h, cache_read";
    private const string EventsSelect = "SELECT " + EventColumns + " FROM usage_events NOT INDEXED";
    private const string EventsInsert = "INSERT OR IGNORE INTO usage_events (" + EventColumns + ") VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10)";

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly bool _recoverCorruption;
    private readonly object _sync = new();
    private bool _initialised;

    /// <param name="databasePath">The database file; its folder is created when missing.</param>
    /// <param name="logger">Receives the one warning a rebuild produces.</param>
    /// <param name="recoverCorruption">
    /// False for a process that shares the file with a running app (a screenshot run): it must never move the file
    /// out from under the owner, so a damaged database is simply used as it is and its errors surface as usual.
    /// </param>
    public SqliteStore(string databasePath, ILogger<SqliteStore>? logger = null, bool recoverCorruption = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _logger = logger ?? NullLogger<SqliteStore>.Instance;
        _recoverCorruption = recoverCorruption;
    }

    public string? ActiveAccountKey { get; set; }
    private static string HistoryKey(string key, string? accountKey) => accountKey is null ? key : accountKey + "|" + key;
    public string DatabasePath { get; }

    /// <summary>Where the damaged file was set aside when this process had to rebuild it; null when it did not.</summary>
    public string? RecoveredFrom { get; private set; }

    /// <summary>History rows and usage events copied from the damaged file into the rebuilt one.</summary>
    public int SalvagedRows { get; private set; }

    public ScanState? GetScanState(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset, length, mtime_ticks, context FROM scan_state WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ScanState(path, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }

    public void SetScanState(ScanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_state (path, offset, length, mtime_ticks, context) VALUES ($path, $offset, $length, $mtime, $context)
            ON CONFLICT(path) DO UPDATE SET offset = excluded.offset, length = excluded.length, mtime_ticks = excluded.mtime_ticks, context = excluded.context
            """;
        command.Parameters.AddWithValue("$path", state.Path);
        command.Parameters.AddWithValue("$offset", state.Offset);
        command.Parameters.AddWithValue("$length", state.Length);
        command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);
        command.Parameters.AddWithValue("$context", (object?)state.Context ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public int InsertEvents(IReadOnlyCollection<UsageEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return 0;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO usage_events
                (message_id, request_id, timestamp_ticks, model, project, session_id, input, output, cache_write_5m, cache_write_1h, cache_read)
            VALUES ($message, $request, $ticks, $model, $project, $session, $input, $output, $cw5m, $cw1h, $read)
            """;
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var request = command.Parameters.Add("$request", SqliteType.Text);
        var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
        var model = command.Parameters.Add("$model", SqliteType.Text);
        var project = command.Parameters.Add("$project", SqliteType.Text);
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var input = command.Parameters.Add("$input", SqliteType.Integer);
        var output = command.Parameters.Add("$output", SqliteType.Integer);
        var cw5m = command.Parameters.Add("$cw5m", SqliteType.Integer);
        var cw1h = command.Parameters.Add("$cw1h", SqliteType.Integer);
        var read = command.Parameters.Add("$read", SqliteType.Integer);

        var inserted = 0;
        foreach (var usageEvent in events)
        {
            message.Value = usageEvent.MessageId;
            request.Value = usageEvent.RequestId;
            ticks.Value = usageEvent.Timestamp.UtcTicks;
            model.Value = usageEvent.Model;
            project.Value = (object?)usageEvent.Project ?? DBNull.Value;
            session.Value = (object?)usageEvent.SessionId ?? DBNull.Value;
            input.Value = usageEvent.InputTokens;
            output.Value = usageEvent.OutputTokens;
            cw5m.Value = usageEvent.CacheWrite5mTokens;
            cw1h.Value = usageEvent.CacheWrite1hTokens;
            read.Value = usageEvent.CacheReadTokens;
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return inserted;
    }

    public long CountEvents()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_events";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<ModelTotals> TotalsByModel(DateTimeOffset since, DateTimeOffset until)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, SUM(input), SUM(output), SUM(cache_write_5m), SUM(cache_write_1h), SUM(cache_read), COUNT(*)
            FROM usage_events WHERE timestamp_ticks >= $from AND timestamp_ticks < $to
            GROUP BY model ORDER BY SUM(input + output + cache_write_5m + cache_write_1h + cache_read) DESC
            """;
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<ModelTotals>();
        while (reader.Read())
        {
            result.Add(new ModelTotals(reader.GetString(0), ReadTotals(reader, 1)));
        }

        return result;
    }

    public IReadOnlyList<ProjectUsage> TopProjects(DateTimeOffset since, DateTimeOffset until, int count)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(project, ''), SUM(input + output + cache_write_5m + cache_write_1h + cache_read) AS tokens
            FROM usage_events WHERE timestamp_ticks >= $from AND timestamp_ticks < $to
            GROUP BY project ORDER BY tokens DESC LIMIT $count
            """;
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        var result = new List<ProjectUsage>();
        while (reader.Read())
        {
            result.Add(new ProjectUsage(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    public IReadOnlyList<DailyModelTotals> DailyTotals(DateTimeOffset since, DateTimeOffset until, TimeSpan localOffset)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (timestamp_ticks + $offset) / $day AS day, model,
                   SUM(input), SUM(output), SUM(cache_write_5m), SUM(cache_write_1h), SUM(cache_read), COUNT(*)
            FROM usage_events WHERE timestamp_ticks >= $from AND timestamp_ticks < $to
            GROUP BY day, model ORDER BY day, model
            """;
        command.Parameters.AddWithValue("$offset", localOffset.Ticks);
        command.Parameters.AddWithValue("$day", TimeSpan.TicksPerDay);
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<DailyModelTotals>();
        while (reader.Read())
        {
            var day = DateOnly.FromDateTime(new DateTime(reader.GetInt64(0) * TimeSpan.TicksPerDay, DateTimeKind.Unspecified));
            result.Add(new DailyModelTotals(day, reader.GetString(1), ReadTotals(reader, 2)));
        }

        return result;
    }

    public void AppendSnapshot(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Windows.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A history recorder already handling an older poll must not repopulate rows after clear.
        command.CommandText = "SELECT value FROM analytics_metadata WHERE key = 'history_cleared_at_ticks'";
        if (command.ExecuteScalar() is string cleared && long.TryParse(cleared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clearedTicks)
            && snapshot.LastUpdated.UtcTicks <= clearedTicks)
        {
            transaction.Commit();
            return;
        }

        command.CommandText = """
            INSERT OR REPLACE INTO snapshot_history (timestamp_ticks, window_key, percent, resets_at_ticks)
            VALUES ($ticks, $key, $percent, $resets)
            """;
        var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var percent = command.Parameters.Add("$percent", SqliteType.Real);
        var resets = command.Parameters.Add("$resets", SqliteType.Integer);
        foreach (var window in snapshot.Windows)
        {
            ticks.Value = snapshot.LastUpdated.UtcTicks;
            key.Value = snapshot.AccountKey is null ? window.Key : snapshot.AccountKey + "|" + window.Key;
            percent.Value = window.UtilizationPercent;
            resets.Value = window.ResetsAt is { } at ? at.UtcTicks : DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<HistoryPoint> GetSeries(string windowKey, DateTimeOffset since, DateTimeOffset until) =>
        GetSeries(windowKey, since, until, ActiveAccountKey);

    public IReadOnlyList<HistoryPoint> GetSeries(string windowKey, DateTimeOffset since, DateTimeOffset until, string? accountKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_ticks, percent, resets_at_ticks FROM snapshot_history
            WHERE window_key = $key AND timestamp_ticks >= $from AND timestamp_ticks < $to ORDER BY timestamp_ticks
            """;
        command.Parameters.AddWithValue("$key", HistoryKey(windowKey, accountKey));
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<HistoryPoint>();
        while (reader.Read())
        {
            result.Add(new HistoryPoint(
                new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                reader.GetDouble(1),
                reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)));
        }

        return result;
    }

    public IReadOnlyList<string> GetWindowKeys() => GetWindowKeys(ActiveAccountKey);

    public IReadOnlyList<string> GetWindowKeys(string? accountKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT window_key FROM snapshot_history ORDER BY window_key";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (accountKey is null) { if (!key.Contains('|', StringComparison.Ordinal)) { result.Add(key); } }
            else if (key.StartsWith(accountKey + "|", StringComparison.Ordinal)) { result.Add(key[(accountKey.Length + 1)..]); }
        }

        return result;
    }

    public int Prune(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshot_history WHERE timestamp_ticks < $before; DELETE FROM usage_events WHERE timestamp_ticks < $before;";
        command.Parameters.AddWithValue("$before", before.UtcTicks);
        return command.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM snapshot_history; DELETE FROM usage_events;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public DateTimeOffset? GetHistoryClearedAt()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM analytics_metadata WHERE key = 'history_cleared_at_ticks'";
        return command.ExecuteScalar() is string value && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    public void ClearAndBaseline(IReadOnlyCollection<ScanState> states, DateTimeOffset clearedAt)
    {
        ArgumentNullException.ThrowIfNull(states);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM snapshot_history; DELETE FROM usage_events; DELETE FROM scan_state;";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO scan_state (path, offset, length, mtime_ticks, context) VALUES ($path, $offset, $length, $mtime, $context)";
        foreach (var state in states)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$path", state.Path);
            command.Parameters.AddWithValue("$offset", state.Offset);
            command.Parameters.AddWithValue("$length", state.Length);
            command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);
            command.Parameters.AddWithValue("$context", (object?)state.Context ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO analytics_metadata (key, value) VALUES ('history_cleared_at_ticks', $ticks) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$ticks", clearedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Why the file cannot be trusted, or null when it is sound or does not exist yet. Only a definite verdict counts:
    /// SQLite reporting corruption, or a file that is not a database. A busy or locked file is not damage.
    /// </summary>
    internal static string? FindDamage(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(Unpooled(databasePath, SqliteOpenMode.ReadWrite));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1)";
            var verdict = command.ExecuteScalar() as string;
            return string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase) ? null : verdict ?? "quick_check gave no answer";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return ex.Message;
        }
    }

    /// <summary>history.db becomes history.corrupt-20260910-064935.db, with a counter should that name be taken.</summary>
    internal static string AsidePath(string databasePath, DateTime now)
    {
        var directory = Path.GetDirectoryName(databasePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(databasePath) + ".corrupt-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var extension = Path.GetExtension(databasePath);
        var candidate = Path.Combine(directory, stem + extension);
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, stem + "-" + i.ToString(CultureInfo.InvariantCulture) + extension);
        }

        return candidate;
    }

    private static string Unpooled(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    private static TokenTotals ReadTotals(SqliteDataReader reader, int start) => new(
        reader.GetInt64(start),
        reader.GetInt64(start + 1),
        reader.GetInt64(start + 2),
        reader.GetInt64(start + 3),
        reader.GetInt64(start + 4),
        reader.GetInt32(start + 5));

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";
        command.ExecuteNonQuery();

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"History database schema version {version} is newer than this app supports.");
        }

        if (version < 1)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS scan_state (
                    path TEXT PRIMARY KEY, offset INTEGER NOT NULL, length INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                    context TEXT);
                CREATE TABLE IF NOT EXISTS analytics_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS usage_events (
                    message_id TEXT NOT NULL, request_id TEXT NOT NULL, timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL,
                    project TEXT, session_id TEXT, input INTEGER NOT NULL, output INTEGER NOT NULL,
                    cache_write_5m INTEGER NOT NULL, cache_write_1h INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                    PRIMARY KEY (message_id, request_id));
                CREATE INDEX IF NOT EXISTS ix_usage_events_time ON usage_events (timestamp_ticks);
                CREATE TABLE IF NOT EXISTS snapshot_history (
                    timestamp_ticks INTEGER NOT NULL, window_key TEXT NOT NULL, percent REAL NOT NULL, resets_at_ticks INTEGER,
                    PRIMARY KEY (timestamp_ticks, window_key));
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            version = 1;
        }

        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS notification_crossings (
                    account_key TEXT NOT NULL, window_key TEXT NOT NULL, reset_ticks INTEGER NOT NULL,
                    threshold INTEGER NOT NULL, PRIMARY KEY (account_key, window_key, reset_ticks, threshold));
                CREATE INDEX IF NOT EXISTS ix_notification_crossings_reset ON notification_crossings(reset_ticks);
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        EnsureInitialised();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Once per process, before any connection is handed out: check the file, rebuild it if damaged, create the schema.</summary>
    private void EnsureInitialised()
    {
        lock (_sync)
        {
            if (_initialised)
            {
                return;
            }

            if (_recoverCorruption && FindDamage(DatabasePath) is { } damage)
            {
                Rebuild(damage);
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            CreateSchema(connection);
            _initialised = true;
        }
    }

    private void Rebuild(string damage)
    {
        var aside = AsidePath(DatabasePath, DateTime.Now);
        try
        {
            File.Move(DatabasePath, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "history.db is damaged ({Damage}) but could not be set aside, so it stays in use", damage);
            return;
        }

        // The write-ahead log travels with its database: it may hold the newest good pages.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(DatabasePath + suffix))
            {
                try
                {
                    File.Move(DatabasePath + suffix, aside + suffix);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("{File} could not be set aside with the damaged database ({Reason})", Path.GetFileName(DatabasePath) + suffix, ex.GetType().Name);
                }
            }
        }

        RecoveredFrom = aside;
        using var fresh = new SqliteConnection(Unpooled(DatabasePath, SqliteOpenMode.ReadWriteCreate));
        fresh.Open();
        CreateSchema(fresh);

        var history = 0;
        var events = 0;
        try
        {
            using var damaged = new SqliteConnection(Unpooled(aside, SqliteOpenMode.ReadOnly));
            damaged.Open();
            history = Salvage(damaged, fresh, history: true);
            events = Salvage(damaged, fresh, history: false);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning("Nothing could be read from the damaged database ({Reason})", ex.Message);
        }

        SalvagedRows = history + events;
        _logger.LogWarning(
            "history.db was damaged ({Damage}). It was set aside as {Aside} and a fresh database started with the {History} history rows and {Events} usage events that could still be read; the session logs are re-read to fill in the rest",
            damage,
            aside,
            history,
            events);
    }

    /// <summary>Copies rows until the damaged table stops yielding them, and keeps whatever was read before that point.</summary>
    private int Salvage(SqliteConnection damaged, SqliteConnection fresh, bool history)
    {
        var columns = history ? 4 : 11;
        var copied = 0;
        using var transaction = fresh.BeginTransaction();
        using var insert = fresh.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = history ? HistoryInsert : EventsInsert;
        var parameters = new SqliteParameter[columns];
        for (var i = 0; i < columns; i++)
        {
            parameters[i] = insert.Parameters.Add(new SqliteParameter("$p" + i.ToString(CultureInfo.InvariantCulture), DBNull.Value));
        }

        try
        {
            using var select = damaged.CreateCommand();
            select.CommandText = history ? HistorySelect : EventsSelect;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < columns; i++)
                {
                    parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }

                copied += insert.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogDebug("Salvage of {Table} stopped after {Rows} rows: {Reason}", history ? "snapshot_history" : "usage_events", copied, ex.Message);
        }

        transaction.Commit();
        return copied;
    }
}
