using System.Diagnostics;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--tree-child")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var probeRoot = Environment.GetEnvironmentVariable("CODEX_PROCESS_PROBE_ROOT")
    ?? throw new InvalidOperationException("Missing probe root.");
Directory.CreateDirectory(probeRoot);
var self = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");
using var child = Process.Start(new ProcessStartInfo(self)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    ArgumentList = { "--tree-child" },
}) ?? throw new InvalidOperationException("Could not start child process.");
child.StandardInput.Close();

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
    .Where(entry => entry.Key is string)
    .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
File.WriteAllText(Path.Combine(probeRoot, "process.json"), JsonSerializer.Serialize(new
{
    ParentProcessId = Environment.ProcessId,
    ChildProcessId = child.Id,
    Arguments = arguments,
    CodexHome = Environment.GetEnvironmentVariable("CODEX_HOME"),
    SafeProbe = Environment.GetEnvironmentVariable("CODEX_PROCESS_PROBE_SAFE"),
    LeakedThreads = environment.Keys.Where(key => key.StartsWith("CODEX_THREAD", StringComparison.OrdinalIgnoreCase)).ToArray(),
    LeakedApiKey = environment.Keys.Any(key => string.Equals(key, "OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase)),
}, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

while (await Console.In.ReadLineAsync() is { } line)
{
    using var request = JsonDocument.Parse(line);
    if (request.RootElement.TryGetProperty("id", out var id))
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { } }));
        await Console.Out.FlushAsync();
    }
}
