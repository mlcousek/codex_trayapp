using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexTrayApp.Core.Codex;

public interface ICodexRpcClient : IAsyncDisposable
{
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken);
}

public sealed class CodexRpcException(int code) : Exception("Codex account request failed.")
{
    public int Code { get; } = code;
}

public sealed class CodexAccountNotificationEventArgs(string method, JsonElement parameters) : EventArgs
{
    public string Method { get; } = method;
    public JsonElement Parameters { get; } = parameters;
}

internal interface ICodexRpcTransport : IAsyncDisposable
{
    TextReader Output { get; }
    TextReader Errors { get; }
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
}

/// <summary>Owns one hidden account-only connection. Raw output is never logged.</summary>
public sealed class CodexRpcClient : ICodexRpcClient
{
    private readonly ICodexRpcTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private readonly Task _errors;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _nextId;

    public event EventHandler<CodexAccountNotificationEventArgs>? AccountNotificationReceived;

    internal CodexRpcClient(ICodexRpcTransport transport, TimeSpan? requestTimeout = null)
    {
        _transport = transport;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _reader = ReadAsync();
        _errors = DrainErrorsAsync();
    }

    public static async Task<CodexRpcClient> StartAsync(string executable, string home, string workingDirectory, CancellationToken cancellationToken)
        => await StartAsync(executable, home, workingDirectory, null, cancellationToken).ConfigureAwait(false);

    internal static async Task<CodexRpcClient> StartAsync(
        string executable,
        string home,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        foreach (var argument in new[] { "-c", "analytics.enabled=false", "-c", "otel.exporter=\"none\"", "app-server", "--listen", "stdio://" }) { start.ArgumentList.Add(argument); }
        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
            {
                if (value is null) { start.Environment.Remove(key); }
                else { start.Environment[key] = value; }
            }
        }
        start.Environment["CODEX_HOME"] = home;
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("CODEX_THREAD", StringComparison.OrdinalIgnoreCase) || string.Equals(k, "OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) { start.Environment.Remove(key); }
        var client = new CodexRpcClient(new ProcessTransport(Process.Start(start) ?? throw new IOException("Codex could not start.")));
        return await InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CodexRpcClient> InitializeAsync(CodexRpcClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex_usage_tray", title = "Codex Usage Tray", version = "0.1.0" },
                capabilities = new { experimentalApi = false },
            }, cancellationToken).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(client._requestTimeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, client._lifetime.Token, timeout.Token);
            try
            {
                await client.WriteAsync(new { method = "initialized", @params = new { } }, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (timeout.IsCancellationRequested) { throw new TimeoutException("Codex initialization timed out."); }
                throw new IOException("Codex connection closed.");
            }
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        using var timeout = new CancellationTokenSource(_requestTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, timeout.Token);
        try
        {
            if (_lifetime.IsCancellationRequested) { throw new IOException("Codex connection closed."); }
            await WriteAsync(new { id, method, @params = parameters }, deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (timeout.IsCancellationRequested) { throw new TimeoutException("Codex account request timed out."); }
            throw new IOException("Codex connection closed.");
        }
        finally { _requests.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.WriteLineAsync(JsonSerializer.Serialize(value), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // A partial or blocked record cannot safely share this transport with another write.
            await _lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally { _writes.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _transport.Output.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { continue; }
                    if (!root.TryGetProperty("id", out var value))
                    {
                        PublishNotification(root);
                        continue;
                    }
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || !_requests.TryGetValue(id, out var pending)) { continue; }
                    if (root.TryGetProperty("error", out var error))
                    {
                        var codeValue = -1;
                        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var parsedCode)) { codeValue = parsedCode; }
                        pending.TrySetException(new CodexRpcException(codeValue));
                    }
                    else if (root.TryGetProperty("result", out var result)) { pending.TrySetResult(result.Clone()); }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            foreach (var pending in _requests.Values) { pending.TrySetException(new IOException("Codex connection closed.")); }
        }
    }

    private void PublishNotification(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) { return; }
        var name = method.GetString();
        if (name is not ("account/updated" or "account/rateLimits/updated") || !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) { return; }
        var args = new CodexAccountNotificationEventArgs(name, parameters.Clone());
        var handlers = AccountNotificationReceived;
        if (handlers is null) { return; }
        foreach (EventHandler<CodexAccountNotificationEventArgs> handler in handlers.GetInvocationList())
        {
            // A consumer failure must not terminate response correlation for other requests.
            try { handler(this, args); }
            catch (Exception) { }
        }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (await _transport.Errors.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is not null) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) { return new ValueTask(_disposeTask ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var pending in _requests.Values) { pending.TrySetException(new IOException("Codex connection closed.")); }
        await _transport.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_reader, _errors).ConfigureAwait(false);
        // Requests may still be unwinding; retaining the small synchronization objects avoids
        // racing their finally blocks or a late caller with disposed semaphores/tokens.
    }

    private sealed class ProcessTransport(Process process) : ICodexRpcTransport
    {
        public TextReader Output => process.StandardOutput;
        public TextReader Errors => process.StandardError;
        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            finally { process.Dispose(); }
        }
    }
}
