using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Settings;

/// <summary>
/// Owns settings.json: loads it (writing the defaults on first run so the file is there to edit), saves changes
/// atomically, and reloads when something else edits the file. A file that cannot be parsed is left untouched and
/// reported through <see cref="LastError"/>; the previous settings stay in force.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogger<SettingsStore> _logger;
    private readonly TimeSpan _debounce;
    private readonly object _sync = new();
    private AppSettings _current = AppSettings.Default;
    private string? _lastWritten;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _pendingReload;

    public SettingsStore(string path, ILogger<SettingsStore> logger, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
    }

    /// <summary>Raised after the settings changed, from a save or an external edit. May fire on any thread.</summary>
    public event EventHandler<AppSettings>? Changed;

    public string Path { get; }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Why the last load or save failed, in user-facing words; null when the last operation succeeded.</summary>
    public string? LastError { get; private set; }

    /// <summary>Reads the file, or writes the defaults when there is none. Returns the settings in force afterwards.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            _logger.LogInformation("No settings file at {Path}; writing the defaults", Path);
            Save(AppSettings.Default);
            return Current;
        }

        Reload();
        return Current;
    }

    /// <summary>Re-reads the file. Returns true when the settings in force changed.</summary>
    public bool Reload()
    {
        string text;
        try
        {
            text = ReadWithRetry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "settings.json could not be read: " + ex.Message;
            _logger.LogWarning(ex, "Settings file {Path} could not be read; keeping the current settings", Path);
            return false;
        }

        if (text == _lastWritten)
        {
            return false;
        }

        AppSettings parsed;
        try
        {
            parsed = (JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? AppSettings.Default).Normalized();
        }
        catch (JsonException ex)
        {
            LastError = "settings.json is not valid JSON; the file was left as it is and the previous settings stay in force. " + ex.Message;
            _logger.LogWarning(ex, "Settings file {Path} is not valid JSON; keeping the current settings", Path);
            return false;
        }

        LastError = null;
        return Publish(parsed);
    }

    /// <summary>Normalizes and writes the settings, then raises <see cref="Changed"/> if anything differs.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalized();
        var text = JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;

        try
        {
            WriteAtomically(text);
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "settings.json could not be written: " + ex.Message;
            _logger.LogWarning(ex, "Settings file {Path} could not be written", Path);
        }

        Publish(normalized);
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Save(change(Current));
    }

    /// <summary>Starts reloading on external edits, debounced so an editor's several writes count once.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file watcher could not be started; edits to {Path} apply after a restart", Path);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pendingReload?.Cancel();
        _pendingReload?.Dispose();
    }

    private bool Publish(AppSettings settings)
    {
        lock (_sync)
        {
            if (_current == settings)
            {
                return false;
            }

            _current = settings;
        }

        Changed?.Invoke(this, settings);
        return true;
    }

    private string ReadWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private void WriteAtomically(string text)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, Path, overwrite: true);
        _lastWritten = text;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        CancellationTokenSource source;
        lock (_sync)
        {
            _pendingReload?.Cancel();
            _pendingReload?.Dispose();
            _pendingReload = source = new CancellationTokenSource();
        }

        var token = source.Token;
        _ = Task.Delay(_debounce, token).ContinueWith(
            _ =>
            {
                if (!token.IsCancellationRequested && Reload())
                {
                    _logger.LogInformation("Settings reloaded from {Path}", Path);
                }
            },
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }
}
