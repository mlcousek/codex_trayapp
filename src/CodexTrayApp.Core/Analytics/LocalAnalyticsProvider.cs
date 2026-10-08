using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Analytics;

/// <summary>
/// Keeps the store current: a full incremental scan at start, a rescan two seconds after the session logs change
/// (debounced), and a safety-net rescan every few minutes. Works with no network and no Codex CLI sign-in.
/// </summary>
public sealed class LocalAnalyticsProvider : IDisposable
{
    private readonly JsonlScanner _scanner;
    private readonly string _projectsDirectory;
    private readonly ILogger<LocalAnalyticsProvider> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _safetyInterval;
    private readonly SemaphoreSlim _signal = new(0);
    private FileSystemWatcher? _watcher;

    public LocalAnalyticsProvider(
        JsonlScanner scanner,
        string projectsDirectory,
        ILogger<LocalAnalyticsProvider> logger,
        TimeSpan? debounce = null,
        TimeSpan? safetyInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsDirectory);
        _scanner = scanner;
        _projectsDirectory = projectsDirectory;
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _safetyInterval = safetyInterval ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Raised after a scan that added events, and once after the initial scan even when it added none.</summary>
    public event EventHandler? DataChanged;

    public ScanResult? LastScan { get; private set; }

    public bool ProjectsDirectoryExists => Directory.Exists(_projectsDirectory);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        RunScan(force: true, cancellationToken);
        StartWatcher();

        while (!cancellationToken.IsCancellationRequested)
        {
            var signalled = await _signal.WaitAsync(_safetyInterval, cancellationToken).ConfigureAwait(false);
            if (signalled)
            {
                await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
                while (_signal.CurrentCount > 0)
                {
                    _signal.Wait(0, cancellationToken);
                }
            }

            RunScan(force: false, cancellationToken);
        }
    }

    /// <summary>Asks for a rescan soon (debounced), for example from a manual refresh.</summary>
    public void RequestScan() => _signal.Release();

    /// <summary>Clears derived history without importing existing session activity again.</summary>
    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => _scanner.ClearHistory(cancellationToken), cancellationToken).ConfigureAwait(false);
        LastScan = null;
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _signal.Dispose();
    }

    private void RunScan(bool force, CancellationToken cancellationToken)
    {
        try
        {
            var result = _scanner.Scan(cancellationToken);
            LastScan = result;
            if (force || result.NewEvents > 0)
            {
                DataChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session log scan failed");
        }
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_projectsDirectory))
        {
            _logger.LogInformation("No Codex CLI session logs at {Directory}; local analytics stay empty until they appear", _projectsDirectory);
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_projectsDirectory, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += (_, e) => _logger.LogWarning("Session log watcher error: {Reason}", e.GetException().GetType().Name);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Session log watcher could not start ({Reason}); falling back to periodic scans", ex.GetType().Name);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }
}
