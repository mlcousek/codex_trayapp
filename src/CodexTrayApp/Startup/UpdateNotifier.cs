using CodexTrayApp.Core.Updates;

namespace CodexTrayApp.Startup;

/// <summary>
/// The last thing the update check learned, shared between the background service that performs the check and the
/// settings window that reports it. Deliberately tiny: the check itself lives in Core, the tray notification is
/// raised by the service, and this only remembers the answer so a window opened later can still show it.
/// </summary>
public sealed class UpdateNotifier
{
    private readonly object _sync = new();
    private UpdateCheckOutcome? _last;
    private DateTimeOffset? _lastChecked;

    /// <summary>Raised after every recorded check. May fire on a background thread.</summary>
    public event EventHandler? Changed;

    public UpdateCheckOutcome? Last
    {
        get
        {
            lock (_sync)
            {
                return _last;
            }
        }
    }

    public DateTimeOffset? LastChecked
    {
        get
        {
            lock (_sync)
            {
                return _lastChecked;
            }
        }
    }

    /// <summary>The release worth telling the user about, or null when this build is current or the check failed.</summary>
    public ReleaseInfo? Available => Last is { Status: UpdateCheckStatus.UpdateAvailable, Release: { } release } ? release : null;

    public void Report(UpdateCheckOutcome outcome, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_sync)
        {
            _last = outcome;
            _lastChecked = outcome.Status == UpdateCheckStatus.Unavailable ? null : now;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
