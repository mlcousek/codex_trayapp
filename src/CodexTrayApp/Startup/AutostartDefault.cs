using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Startup;

/// <summary>What <see cref="AutostartDefault.Apply"/> did, which is also what the log line says.</summary>
public enum AutostartDefaultOutcome
{
    /// <summary>The default was applied on an earlier launch; whatever the entry says now is the user's choice.</summary>
    AlreadyDecided,

    /// <summary>The entry was already there, so only the marker was written.</summary>
    AlreadyEnabled,

    /// <summary>The entry was created.</summary>
    Enabled,

    /// <summary>Windows refused the write; nothing is recorded, so the next launch tries again.</summary>
    Refused,
}

/// <summary>
/// Turns "Start with Windows" on the first time the app runs, and never again. A tray app that has to be started by
/// hand after every boot is not doing its job, so the entry is created once; from then on the checkbox in the
/// settings window and the tray menu are the only things that change it, and a user who switches it off stays off.
/// </summary>
public sealed class AutostartDefault
{
    private readonly IAutostartEntry _entry;
    private readonly ILogger<AutostartDefault> _logger;

    public AutostartDefault(IAutostartEntry entry, ILogger<AutostartDefault> logger)
    {
        _entry = entry;
        _logger = logger;
    }

    /// <summary>Applies the default at most once. Safe to call on every launch.</summary>
    public AutostartDefaultOutcome Apply()
    {
        if (_entry.WasDefaultApplied())
        {
            return AutostartDefaultOutcome.AlreadyDecided;
        }

        if (_entry.IsEnabled())
        {
            _entry.MarkDefaultApplied();
            _logger.LogInformation("Start with Windows was already on; the default is now recorded as applied");
            return AutostartDefaultOutcome.AlreadyEnabled;
        }

        if (!_entry.TrySet(true, out var error))
        {
            // Nothing is marked, so the next launch tries again rather than leaving the app quietly opt-in forever.
            _logger.LogWarning("Start with Windows could not be turned on by default: {Error}", error);
            return AutostartDefaultOutcome.Refused;
        }

        _entry.MarkDefaultApplied();
        _logger.LogInformation("Start with Windows turned on by default; switch it off in Settings to stop that");
        return AutostartDefaultOutcome.Enabled;
    }
}
