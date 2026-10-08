namespace CodexTrayApp.Startup;

/// <summary>
/// The registry side of "Start with Windows", behind an interface so everything above it can be tested without a
/// real HKCU write. <see cref="AutostartManager"/> is the only implementation that touches the registry.
/// </summary>
public interface IAutostartEntry
{
    /// <summary>True when the Run entry exists and points at this executable.</summary>
    bool IsEnabled();

    /// <summary>Creates or removes the Run entry. False with a user-facing reason when the registry refuses.</summary>
    bool TrySet(bool enabled, out string? reason);

    /// <summary>True once the app has applied its start-with-Windows default, so it never does so twice.</summary>
    bool WasDefaultApplied();

    /// <summary>Records that the default has been applied. Failure is not fatal: the default is simply tried again.</summary>
    void MarkDefaultApplied();
}
