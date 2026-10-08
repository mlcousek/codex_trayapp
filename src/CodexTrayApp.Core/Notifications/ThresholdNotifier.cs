using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Core.Notifications;

/// <summary>One crossing worth telling the user about: the highest threshold the window has passed this period.</summary>
public sealed record ThresholdAlert(string WindowKey, string WindowName, int Threshold, double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// Claims crossings before returning alerts, so delivery is at most once even across restarts.
/// </summary>
public sealed class ThresholdNotifier
{
    public static readonly TimeSpan MaximumSampleAge = TimeSpan.FromMinutes(10);
    private readonly INotificationStore _store;

    public ThresholdNotifier(INotificationStore? store = null) => _store = store ?? new MemoryNotificationStore();

    /// <summary>Alerts for this snapshot, given the enabled thresholds. An empty threshold list never alerts.</summary>
    public IReadOnlyList<ThresholdAlert> Evaluate(UsageSnapshot snapshot, IReadOnlyList<int> thresholds, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);

        var currentTime = now ?? DateTimeOffset.UtcNow;
        var enabled = thresholds.Where(t => t is > 0 and <= 100).Distinct().ToArray();
        if (enabled.Length == 0 || snapshot.Source != UsageSource.Live || snapshot.LastUpdated > currentTime ||
            currentTime - snapshot.LastUpdated > MaximumSampleAge ||
            string.IsNullOrWhiteSpace(snapshot.AccountKey) || string.IsNullOrWhiteSpace(snapshot.AccountIdentityKey) ||
            string.IsNullOrWhiteSpace(snapshot.AccountSourceKey))
        {
            return [];
        }

        // The ledger never receives account names, addresses, source paths or provider identifiers.
        var account = Fingerprint(JsonSerializer.Serialize(new[] { snapshot.AccountKey, snapshot.AccountIdentityKey, snapshot.AccountSourceKey }));
        var alerts = new List<ThresholdAlert>();
        foreach (var window in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.Key) || window.ResetsAt is not { } reset || reset <= currentTime ||
                !double.IsFinite(window.UtilizationPercent))
            {
                continue;
            }

            var crossed = enabled.Where(t => window.UtilizationPercent >= t).ToArray();
            if (crossed.Length == 0)
            {
                continue;
            }

            var claimed = _store.TryMarkCrossings(account, Fingerprint(window.Key.ToUpperInvariant()), reset, crossed, currentTime);
            if (claimed is { } threshold)
            {
                alerts.Add(new ThresholdAlert(window.Key, window.DisplayName, threshold, window.UtilizationPercent, reset));
            }
        }

        return alerts;
    }

    /// <summary>Resets transient test state. Durable notification claims are intentionally never erased.</summary>
    public void Reset()
    {
        if (_store is MemoryNotificationStore memory)
        {
            memory.Reset();
        }
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
