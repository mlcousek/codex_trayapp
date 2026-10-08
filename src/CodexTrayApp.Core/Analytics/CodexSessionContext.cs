using System.Globalization;
using System.Text.Json;

namespace CodexTrayApp.Core.Analytics;

/// <summary>Only numerical usage and session metadata survive ingestion.</summary>
public sealed class CodexSessionContext
{
    public string? SessionId { get; set; }
    public string? Project { get; set; }
    public string Model { get; set; } = "unknown";
    public long Input { get; set; }
    public long Output { get; set; }
    public long Cached { get; set; }
    public long CacheWrite { get; set; }
    public long CounterEpoch { get; set; }

    public UsageEvent? Parse(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) { return null; }
            var type = Text(root, "type");
            if (type == "session_meta")
            {
                var sessionId = Text(payload, "id") ?? Text(payload, "session_id");
                if (sessionId != SessionId)
                {
                    Input = Output = Cached = CacheWrite = CounterEpoch = 0;
                    Model = "unknown";
                }
                SessionId = sessionId;
                Project = Text(payload, "cwd");
                return null;
            }
            if (type == "turn_context") { Model = Text(payload, "model") ?? "unknown"; Project = Text(payload, "cwd") ?? Project; return null; }
            if (type != "event_msg" || Text(payload, "type") != "token_count" || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("total_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object) { return null; }
            if (!DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) || SessionId is null) { return null; }
            // An invalid counter must not erase the saved baseline and turn the next valid total into new usage.
            if (!Number(usage, "input_tokens", out var input, required: true)
                || !Number(usage, "output_tokens", out var output, required: true)
                || !Number(usage, "cached_input_tokens", out var rawCached)
                || !Number(usage, "cache_write_input_tokens", out var cacheWrite)) { return null; }
            var cached = Math.Min(input, rawCached);
            // Decreases indicate a new counter epoch; its first total is a baseline, not new activity.
            if (input < Input || output < Output || cached < Cached || cacheWrite < CacheWrite)
            {
                Input = input; Output = output; Cached = cached; CacheWrite = cacheWrite;
                CounterEpoch++;
                return null;
            }
            var di = input - Input; var dout = output - Output; var dc = Math.Min(di, cached - Cached); var dw = cacheWrite - CacheWrite;
            Input = input; Output = output; Cached = cached; CacheWrite = cacheWrite;
            if (di == 0 && dout == 0 && dw == 0) { return null; }
            // Cumulative identity deduplicates copies of the same session prefix across files.
            var id = string.Create(CultureInfo.InvariantCulture, $"{SessionId}:{input}:{output}:{cached}:{cacheWrite}");
            if (CounterEpoch > 0) { id += string.Create(CultureInfo.InvariantCulture, $":epoch:{CounterEpoch}"); }
            return new UsageEvent(id, string.Empty, timestamp, Model, Project, SessionId, di - dc, dout, dw, 0, dc);
        }
        catch (JsonException) { return null; }
    }
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Number(JsonElement element, string key, out long number, bool required = false)
    {
        number = 0;
        if (!element.TryGetProperty(key, out var value)) { return !required; }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number) && number >= 0;
    }
}
