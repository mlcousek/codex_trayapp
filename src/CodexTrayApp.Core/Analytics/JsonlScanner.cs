using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodexTrayApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Analytics;

/// <summary>
/// Incremental scan of every session log under the Codex CLI projects folder. Each file's byte offset, length
/// and mtime live in the store, so a rescan reads only appended bytes; a shrunken file is read again from the start.
/// Locked files and malformed lines are skipped, never fatal.
/// </summary>
public sealed class JsonlScanner
{
    private const int ChunkSize = 1024 * 1024;
    private static readonly string[] SessionFolders = ["sessions", "archived_sessions"];
    private readonly string _projectsDirectory;
    private readonly IAnalyticsStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<JsonlScanner> _logger;
    private readonly object _ingestionSync = new();

    private sealed class CursorContext
    {
        public CodexSessionContext Session { get; set; } = new();
        public bool SkipNextRecord { get; set; }
        public int FingerprintLength { get; set; }
        public string? Fingerprint { get; set; }
    }

    public JsonlScanner(string projectsDirectory, IAnalyticsStore store, TimeProvider clock, ILogger<JsonlScanner> logger, TimeSpan? maxAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsDirectory);
        _projectsDirectory = projectsDirectory;
        _store = store;
        _clock = clock;
        _logger = logger;
        MaxAge = maxAge;
    }

    /// <summary>Files older than this are skipped on first sight; null scans everything. Follows the retention setting.</summary>
    public TimeSpan? MaxAge { get; set; }

    public ScanResult Scan(CancellationToken cancellationToken)
    {
        lock (_ingestionSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ScanCore(cancellationToken);
        }
    }

    /// <summary>
    /// Baselines every source, including files that retention or an earlier scan has not consumed.
    /// Scans and clear share one gate; the store commits the deletion and new baselines together.
    /// Source read failures leave the previous history intact rather than allowing a later backfill.
    /// </summary>
    public void ClearHistory(CancellationToken cancellationToken = default)
    {
        lock (_ingestionSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clearedAt = _clock.GetUtcNow();
            var states = new List<ScanState>();
            foreach (var path in EnumeratePaths())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = new CursorContext();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize);
                var length = stream.Length;
                var consumed = ReadCompleteLines(stream, 0, [], context.Session, collectEvents: false, end: length, cancellationToken: cancellationToken);
                // Keep the start of a pre-clear partial record so its eventual completion can update
                // cumulative context, while suppressing that record's event exactly once.
                context.SkipNextRecord = consumed < length;
                SetFingerprint(stream, consumed, context);
                states.Add(new ScanState(path, consumed, length, File.GetLastWriteTimeUtc(path).Ticks, JsonSerializer.Serialize(context)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _store.ClearAndBaseline(states, clearedAt);
        }
    }

    private string[] EnumeratePaths() => !Directory.Exists(_projectsDirectory) ? [] :
        SessionFolders.Select(folder => Path.Combine(_projectsDirectory, folder)).Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories)).ToArray();

    private ScanResult ScanCore(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_projectsDirectory))
        {
            return ScanResult.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        var files = 0;
        var changed = 0;
        var inserted = 0;
        var skipped = 0;
        var cutoff = MaxAge is { } age ? _clock.GetUtcNow() - age : (DateTimeOffset?)null;

        IEnumerable<string> paths;
        try
        {
            paths = EnumeratePaths();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Session logs could not be listed ({Reason})", ex.GetType().Name);
            return ScanResult.Empty;
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files++;
            try
            {
                var outcome = ScanFile(path, cutoff, cancellationToken);
                if (outcome is { } count)
                {
                    changed++;
                    inserted += count;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                _logger.LogDebug("Skipped {File}: {Reason}", Path.GetFileName(path), ex.GetType().Name);
            }
        }

        var result = new ScanResult(files, changed, inserted, skipped, stopwatch.Elapsed);
        _logger.LogInformation(
            "Session log scan: {Files} files, {Changed} changed, {Inserted} new events, {Skipped} skipped, {Elapsed} ms",
            files,
            changed,
            inserted,
            skipped,
            (long)stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>Returns the number of new events, or null when the file was unchanged or too old to matter.</summary>
    private int? ScanFile(string path, DateTimeOffset? cutoff, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var state = _store.GetScanState(path);
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;

        if (state is null && cutoff is { } limit && info.LastWriteTimeUtc < limit.UtcDateTime)
        {
            return null;
        }

        var offset = state?.Offset ?? 0;
        var context = RestoreContext(state?.Context);
        var events = new List<UsageEvent>();
        long consumed;
        long length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize))
        {
            length = stream.Length;
            if (length < (state?.Length ?? offset) || !FingerprintMatches(stream, context))
            {
                offset = 0;
                context = new CursorContext();
            }
            else if (state is not null && state.Length == length && state.LastWriteTicks == lastWriteTicks)
            {
                return null;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            consumed = ReadCompleteLines(stream, offset, events, context.Session, context.SkipNextRecord, end: length, cancellationToken: cancellationToken);
            context.SkipNextRecord = context.SkipNextRecord && consumed == offset;
            SetFingerprint(stream, consumed, context);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_store.GetHistoryClearedAt() is { } clearedAt)
        {
            events.RemoveAll(e => e.Timestamp <= clearedAt);
        }
        var inserted = events.Count > 0 ? _store.InsertEvents(events) : 0;
        _store.SetScanState(new ScanState(path, consumed, length, lastWriteTicks, JsonSerializer.Serialize(context)));
        return inserted;
    }

    private static CursorContext RestoreContext(string? json)
    {
        if (json is null) { return new(); }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(nameof(CursorContext.Session), out _)
            ? JsonSerializer.Deserialize<CursorContext>(json) ?? new()
            : new CursorContext { Session = JsonSerializer.Deserialize<CodexSessionContext>(json) ?? new() };
    }

    private static bool FingerprintMatches(Stream stream, CursorContext context)
    {
        if (context.Fingerprint is null) { return true; }
        if (stream.Length < context.FingerprintLength) { return false; }
        return Fingerprint(stream, context.FingerprintLength) == context.Fingerprint;
    }

    private static void SetFingerprint(Stream stream, long consumed, CursorContext context)
    {
        context.FingerprintLength = (int)Math.Min(consumed, 4096);
        context.Fingerprint = Fingerprint(stream, context.FingerprintLength);
    }

    private static string Fingerprint(Stream stream, int length)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Reads whole lines from <paramref name="start"/>; a trailing partial line is left for the next scan.</summary>
    internal static long ReadCompleteLines(Stream stream, long start, List<UsageEvent> events, CodexSessionContext? context = null,
        bool skipFirstRecord = false, bool collectEvents = true, long? end = null, CancellationToken cancellationToken = default)
    {
        context ??= new CodexSessionContext();
        var buffer = new byte[ChunkSize];
        var carry = Array.Empty<byte>();
        var position = start;
        var consumed = start;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, end is { } limit ? (int)Math.Min(buffer.Length, Math.Max(0, limit - position)) : buffer.Length);
            if (read == 0)
            {
                break;
            }

            var data = carry.Length == 0 ? buffer.AsMemory(0, read) : Concat(carry, buffer.AsSpan(0, read));
            var lineStart = 0;
            var span = data.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] != (byte)'\n')
                {
                    continue;
                }

                var length = i - lineStart;
                if (length > 0 && span[i - 1] == (byte)'\r')
                {
                    length--;
                }

                if (context.Parse(data.Slice(lineStart, length)) is { } usageEvent && collectEvents && !skipFirstRecord)
                {
                    events.Add(usageEvent);
                }

                skipFirstRecord = false;

                lineStart = i + 1;
            }

            position += read;
            consumed = position - (data.Length - lineStart);
            carry = lineStart < data.Length ? data[lineStart..].ToArray() : [];
            if (carry.Length > 8 * ChunkSize) { throw new IOException("Session record exceeds ingestion bound."); }
        }

        return consumed;
    }

    private static Memory<byte> Concat(byte[] carry, ReadOnlySpan<byte> next)
    {
        var joined = new byte[carry.Length + next.Length];
        carry.CopyTo(joined, 0);
        next.CopyTo(joined.AsSpan(carry.Length));
        return joined;
    }
}
