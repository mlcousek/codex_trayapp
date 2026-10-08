using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTrayApp.Core.Domain;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Cache;

public interface ISnapshotCache
{
    Task<UsageSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Forget the active account without deleting other account partitions.</summary>
    Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Atomically persists sanitized snapshots in account partitions. Legacy unpartitioned caches are ignored.
/// </summary>
public sealed class SnapshotCache : ISnapshotCache, IDisposable
{
    private sealed record CacheState(int Version, string? ActiveAccountKey, Dictionary<string, UsageSnapshot> Accounts);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger<SnapshotCache> _logger;
    private readonly SemaphoreSlim _gate = new(1);

    public SnapshotCache(string filePath, ILogger<SnapshotCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<UsageSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            return state.ActiveAccountKey is { } key && state.Accounts.TryGetValue(key, out var snapshot)
                && string.Equals(snapshot.AccountKey, key, StringComparison.Ordinal)
                ? Sanitize(snapshot) with { Source = UsageSource.Cache } : null;
        }
        finally { _gate.Release(); }
    }

    private async Task<CacheState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) { return EmptyState(); }
        try
        {
            var stream = File.OpenRead(_filePath);
            await using (stream.ConfigureAwait(false))
            {
                var state = await JsonSerializer.DeserializeAsync<CacheState>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (state is not { Version: 1, Accounts: not null }) { return EmptyState(); }
                var accounts = new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal);
                foreach (var (key, snapshot) in state.Accounts)
                {
                    if (!string.IsNullOrWhiteSpace(key) && IsValidSnapshot(snapshot)
                        && string.Equals(snapshot.AccountKey, key, StringComparison.Ordinal))
                    {
                        accounts[key] = Sanitize(snapshot);
                    }
                }

                return new CacheState(1, state.ActiveAccountKey is { } active && accounts.ContainsKey(active) ? active : null, accounts);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cached snapshot could not be read ({Reason}); starting without it", ex.GetType().Name);
            return EmptyState();
        }
    }

    public async Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValidSnapshot(snapshot)) { throw new ArgumentException("Snapshot contains invalid cache values.", nameof(snapshot)); }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(snapshot.AccountKey))
            {
                await WriteStateAsync(state with { ActiveAccountKey = null }, cancellationToken).ConfigureAwait(false);
                return;
            }

            state.Accounts[snapshot.AccountKey] = Sanitize(snapshot);
            await WriteStateAsync(state with { ActiveAccountKey = snapshot.AccountKey }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            await WriteStateAsync(state with { ActiveAccountKey = null }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    private static CacheState EmptyState() => new(1, null, new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal));
    private static bool IsValidSnapshot(UsageSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Windows is null || snapshot.LastUpdated == default
            || !Enum.IsDefined(snapshot.Source)) { return false; }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in snapshot.Windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.Key) || string.IsNullOrWhiteSpace(window.DisplayName)
                || !keys.Add(window.Key) || !double.IsFinite(window.UtilizationPercent)
                || window.UtilizationPercent is < 0 or > 100 || !Enum.IsDefined(window.Status)
                || window.DurationMinutes is <= 0) { return false; }
        }

        return snapshot.Overage is not { } overage
            || (overage.UsedAmount is not < 0 && overage.LimitAmount is not < 0
                && (overage.UtilizationPercent is not { } percent || (double.IsFinite(percent) && percent is >= 0 and <= 100)));
    }

    private static UsageSnapshot Sanitize(UsageSnapshot snapshot) => snapshot with
    {
        AccountEmail = null,
        Windows = snapshot.Windows.Select(window => window with
        {
            Status = UsageWindowStatusRules.FromPercent(window.UtilizationPercent),
        }).ToList(),
    };

    private async Task WriteStateAsync(CacheState state, CancellationToken cancellationToken)
    {
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = File.Create(temporary);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Snapshot cache could not be written ({Reason})", ex.GetType().Name);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Snapshot cache temporary file could not be removed ({Reason})", ex.GetType().Name);
            }
        }
    }
}
