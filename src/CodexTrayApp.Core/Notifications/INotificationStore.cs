namespace CodexTrayApp.Core.Notifications;

/// <summary>Atomically records all crossed thresholds and returns only the highest newly claimed crossing.</summary>
public interface INotificationStore
{
    int? TryMarkCrossings(string accountFingerprint, string windowFingerprint, DateTimeOffset reset,
        IReadOnlyCollection<int> thresholds, DateTimeOffset now);
}

internal sealed class MemoryNotificationStore : INotificationStore
{
    private readonly Dictionary<(string Account, string Window, long Reset), HashSet<int>> _claims = [];
    private readonly object _sync = new();

    public int? TryMarkCrossings(string accountFingerprint, string windowFingerprint, DateTimeOffset reset,
        IReadOnlyCollection<int> thresholds, DateTimeOffset now)
    {
        lock (_sync)
        {
            foreach (var key in _claims.Keys.Where(k => k.Reset <= now.UtcTicks).ToArray())
            {
                _claims.Remove(key);
            }

            var keyForPeriod = (accountFingerprint, windowFingerprint, reset.UtcTicks);
            if (!_claims.TryGetValue(keyForPeriod, out var fired))
            {
                fired = [];
                _claims[keyForPeriod] = fired;
            }

            var priorHighest = fired.Count == 0 ? 0 : fired.Max();
            var highest = thresholds.Count == 0 ? 0 : thresholds.Max();
            fired.UnionWith(thresholds);
            return highest > priorHighest ? highest : null;
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _claims.Clear();
        }
    }
}
