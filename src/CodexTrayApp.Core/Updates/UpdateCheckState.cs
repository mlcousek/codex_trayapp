using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Updates;

/// <summary>
/// What the app remembers between runs about update checks: when it last asked, and which version it has already
/// mentioned, so a new release is announced once rather than every week until it is installed.
/// </summary>
public sealed record UpdateCheckState(DateTimeOffset? LastCheckedUtc, string? LastNotifiedVersion)
{
    /// <summary>Scopes the weekly deadline and announcement to this release repository.</summary>
    public string? Repository { get; init; }

    public static UpdateCheckState Empty { get; } = new(null, null);

    /// <summary>True when a check is due: never checked before, the interval has elapsed, or the clock moved back.</summary>
    public bool IsDue(DateTimeOffset now, TimeSpan interval) =>
        LastCheckedUtc is not { } last || now - last >= interval || last > now;
}

/// <summary>
/// Reads and writes the update-check state file. Nothing here is important enough to bother the user about: a
/// missing or unreadable file simply means "never checked".
/// </summary>
public sealed class UpdateCheckStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger<UpdateCheckStateStore> _logger;

    public UpdateCheckStateStore(string path, ILogger<UpdateCheckStateStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger;
    }

    public UpdateCheckState Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UpdateCheckState>(File.ReadAllText(_path), JsonOptions) ?? UpdateCheckState.Empty
                : UpdateCheckState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug("Update check state could not be read ({Reason}); treating it as never checked", ex.GetType().Name);
            return UpdateCheckState.Empty;
        }
    }

    public void Save(UpdateCheckState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Update check state could not be written ({Reason})", ex.GetType().Name);
        }
    }
}
