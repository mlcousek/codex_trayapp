using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;
using CodexTrayApp.Core.Codex;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class CodexRpcClientTests
{
    [Fact]
    public async Task Process_start_is_account_only_and_disposal_kills_its_owned_process_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexTrayApp.ProcessProbe", Guid.NewGuid().ToString("N"));
        var reportDirectory = Path.Combine(root, "report");
        var isolatedHome = Path.Combine(root, "codex-home");
        var workingDirectory = Path.Combine(root, "app-data");
        Directory.CreateDirectory(reportDirectory);
        var executable = Path.Combine(AppContext.BaseDirectory, "ProcessProbe", "CodexTrayApp.ProcessProbe.exe");
        var environment = new Dictionary<string, string?>
        {
            ["CODEX_PROCESS_PROBE_ROOT"] = reportDirectory,
            ["CODEX_PROCESS_PROBE_SAFE"] = "preserved",
            ["cOdEx_ThReAd_synthetic"] = "must-not-inherit",
            ["openai_api_key"] = "synthetic-secret",
        };
        CodexRpcClient? client = null;
        var ownedProcessIds = new List<int>();
        try
        {
            client = await CodexRpcClient.StartAsync(executable, isolatedHome, workingDirectory,
                environment, TestContext.Current.CancellationToken);
            Directory.Exists(workingDirectory).ShouldBeTrue();
            Directory.Exists(isolatedHome).ShouldBeFalse();
            var reportPath = Path.Combine(reportDirectory, "process.json");
            await WaitUntilAsync(() => File.Exists(reportPath), TimeSpan.FromSeconds(5));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            var rootElement = report.RootElement;
            var parentId = rootElement.GetProperty("parentProcessId").GetInt32();
            var childId = rootElement.GetProperty("childProcessId").GetInt32();
            ownedProcessIds.AddRange([parentId, childId]);
            rootElement.GetProperty("codexHome").GetString().ShouldBe(isolatedHome);
            rootElement.GetProperty("safeProbe").GetString().ShouldBe("preserved");
            rootElement.GetProperty("leakedThreads").GetArrayLength().ShouldBe(0);
            rootElement.GetProperty("leakedApiKey").GetBoolean().ShouldBeFalse();
            rootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ShouldBe([
                "-c", "analytics.enabled=false", "-c", "otel.exporter=\"none\"", "app-server", "--listen", "stdio://"]);
            IsProcessAlive(parentId).ShouldBeTrue();
            IsProcessAlive(childId).ShouldBeTrue();

            await client.DisposeAsync();
            await WaitUntilAsync(() => ownedProcessIds.All(id => !IsProcessAlive(id)), TimeSpan.FromSeconds(5));
            ownedProcessIds.ShouldAllBe(id => !IsProcessAlive(id));
        }
        finally
        {
            if (client is not null) { await client.DisposeAsync(); }
            foreach (var processId in ownedProcessIds) { TryKill(processId); }
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed >= timeout) { throw new TimeoutException("Timed out waiting for process probe state."); }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(2_000); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [Fact]
    public async Task Responses_are_correlated_out_of_order_and_unknown_ids_are_ignored()
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport);
        var first = client.RequestAsync("account/read", null, CancellationToken.None);
        var second = client.RequestAsync("account/rateLimits/read", null, CancellationToken.None);
        var firstId = await transport.NextIdAsync();
        var secondId = await transport.NextIdAsync();
        transport.Reply("""{"id":987,"result":"unknown"}""");
        transport.Reply($$"""{"id":{{secondId}},"result":2}""");
        transport.Reply($$"""{"id":{{firstId}},"result":1}""");
        (await first).GetInt32().ShouldBe(1);
        (await second).GetInt32().ShouldBe(2);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("3")]
    [InlineData("{bad")]
    [InlineData("{\"id\":\"one\",\"result\":true}")]
    [InlineData("{\"id\":9999999999999999999999999,\"result\":true}")]
    public async Task Malformed_records_do_not_break_following_response(string record)
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport);
        var request = client.RequestAsync("account/read", null, CancellationToken.None);
        var id = await transport.NextIdAsync();
        transport.Reply(record);
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        (await request).GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"code\":\"bad\"}")]
    [InlineData("{\"code\":1.5}")]
    [InlineData("{\"code\":1e100}")]
    [InlineData("{\"code\":999999999999999999999}")]
    public async Task Invalid_error_codes_produce_safe_exception_and_keep_reader_alive(string error)
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport);
        var request = client.RequestAsync("account/read", null, CancellationToken.None);
        var id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"error":{{error}}}""");
        (await Should.ThrowAsync<CodexRpcException>(() => request)).Code.ShouldBe(-1);
        var next = client.RequestAsync("account/read", null, CancellationToken.None);
        id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        (await next).GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Eof_fails_all_pending_and_future_requests_promptly()
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport);
        var first = client.RequestAsync("account/read", null, CancellationToken.None);
        var second = client.RequestAsync("account/rateLimits/read", null, CancellationToken.None);
        await transport.NextIdAsync();
        await transport.NextIdAsync();
        transport.CloseOutput();
        await Should.ThrowAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<IOException>(() => client.RequestAsync("account/read", null, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_removes_request_and_late_reply_does_not_affect_next_request()
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport);
        using var cancellation = new CancellationTokenSource();
        var request = client.RequestAsync("account/read", null, cancellation.Token);
        var oldId = await transport.NextIdAsync();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => request);
        var next = client.RequestAsync("account/read", null, CancellationToken.None);
        var nextId = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{oldId}},"result":"old"}""");
        transport.Reply($$"""{"id":{{nextId}},"result":"new"}""");
        (await next).GetString().ShouldBe("new");
    }

    [Fact]
    public async Task Response_deadline_expires_without_closing_healthy_connection()
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport, TimeSpan.FromMilliseconds(80));
        var request = client.RequestAsync("account/read", null, CancellationToken.None);
        await transport.NextIdAsync();
        await Should.ThrowAsync<TimeoutException>(() => request.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var next = client.RequestAsync("account/read", null, CancellationToken.None);
        var id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        (await next).GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Deadline_includes_blocked_write_and_write_queue()
    {
        var transport = new FakeTransport { BlockWrites = true };
        await using var client = new CodexRpcClient(transport, TimeSpan.FromMilliseconds(80));
        var first = client.RequestAsync("account/read", null, CancellationToken.None);
        await transport.NextIdAsync();
        var second = client.RequestAsync("account/read", null, CancellationToken.None);
        await Should.ThrowAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var queuedFailure = await Should.ThrowAsync<Exception>(() => second.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        (queuedFailure is IOException or TimeoutException).ShouldBeTrue();
        await Should.ThrowAsync<IOException>(() => client.RequestAsync("account/read", null, CancellationToken.None));
    }

    [Fact]
    public async Task Disposal_is_idempotent_and_cancels_pending_work()
    {
        var transport = new FakeTransport();
        var client = new CodexRpcClient(transport);
        var request = client.RequestAsync("account/read", null, CancellationToken.None);
        await transport.NextIdAsync();
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<IOException>(() => request);
        transport.DisposeCount.ShouldBe(1);
        await Should.ThrowAsync<IOException>(() => client.RequestAsync("account/read", null, CancellationToken.None));
    }

    [Fact]
    public async Task Account_notifications_have_owned_payload_and_consumer_errors_do_not_break_requests()
    {
        var transport = new FakeTransport();
        await using var client = new CodexRpcClient(transport);
        var notification = new TaskCompletionSource<CodexAccountNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AccountNotificationReceived += (_, _) => throw new InvalidOperationException("consumer");
        client.AccountNotificationReceived += (_, args) => notification.TrySetResult(args);
        transport.Reply("""{"method":"thread/started","params":{}}""");
        transport.Reply("""{"method":"account/updated","params":{"planType":"plus"}}""");
        var args = await notification.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        args.Method.ShouldBe("account/updated");
        var request = client.RequestAsync("account/read", null, CancellationToken.None);
        var id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        (await request).GetBoolean().ShouldBeTrue();
        args.Parameters.GetProperty("planType").GetString().ShouldBe("plus");
    }

    [Fact]
    public async Task Handshake_awaits_initialize_reply_before_initialized_notification()
    {
        var transport = new FakeTransport();
        var client = new CodexRpcClient(transport);
        var startup = CodexRpcClient.InitializeAsync(client, TestContext.Current.CancellationToken);
        var initialize = await transport.NextRecordAsync();
        initialize.GetProperty("method").GetString().ShouldBe("initialize");
        initialize.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString().ShouldBe("codex_usage_tray");
        initialize.GetProperty("params").GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean().ShouldBeFalse();
        startup.IsCompleted.ShouldBeFalse();
        var id = initialize.GetProperty("id").GetInt32();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        await using var started = await startup;
        var initialized = await transport.NextRecordAsync();
        initialized.GetProperty("method").GetString().ShouldBe("initialized");
        initialized.TryGetProperty("id", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_handshake_disposes_owned_transport()
    {
        var transport = new FakeTransport();
        var client = new CodexRpcClient(transport);
        var startup = CodexRpcClient.InitializeAsync(client, TestContext.Current.CancellationToken);
        var id = await transport.NextIdAsync();
        transport.Reply($$$"""{"id":{{{id}}},"error":{"code":-32601}}""");
        await Should.ThrowAsync<CodexRpcException>(() => startup);
        transport.DisposeCount.ShouldBe(1);
        await client.DisposeAsync();
        transport.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Initialized_write_timeout_is_reported_as_timeout_and_disposes_transport()
    {
        var transport = new FakeTransport { BlockInitializedWrites = true };
        var client = new CodexRpcClient(transport, TimeSpan.FromMilliseconds(100));
        var startup = CodexRpcClient.InitializeAsync(client, TestContext.Current.CancellationToken);
        var id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        (await transport.NextRecordAsync()).GetProperty("method").GetString().ShouldBe("initialized");
        await Should.ThrowAsync<TimeoutException>(() => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        transport.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Initialized_write_caller_cancellation_remains_cancellation_and_disposes_transport()
    {
        var transport = new FakeTransport { BlockInitializedWrites = true };
        var client = new CodexRpcClient(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var startup = CodexRpcClient.InitializeAsync(client, cancellation.Token);
        var id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        await transport.NextRecordAsync();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        transport.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Initialized_write_transport_closure_is_reported_as_io_failure()
    {
        var transport = new FakeTransport { BlockInitializedWrites = true };
        var client = new CodexRpcClient(transport);
        var startup = CodexRpcClient.InitializeAsync(client, TestContext.Current.CancellationToken);
        var id = await transport.NextIdAsync();
        transport.Reply($$"""{"id":{{id}},"result":true}""");
        await transport.NextRecordAsync();
        transport.CloseOutput();
        await Should.ThrowAsync<IOException>(() => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        transport.DisposeCount.ShouldBe(1);
    }

    private sealed class FakeTransport : ICodexRpcTransport
    {
        private readonly Channel<string> _written = Channel.CreateUnbounded<string>();
        private readonly ChannelReaderText _output = new();
        private readonly ChannelReaderText _errors = new();
        public TextReader Output => _output;
        public TextReader Errors => _errors;
        public bool BlockWrites { get; init; }
        public bool BlockInitializedWrites { get; init; }
        public int DisposeCount { get; private set; }
        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _written.Writer.WriteAsync(line, cancellationToken);
            using var record = JsonDocument.Parse(line);
            if (BlockWrites || (BlockInitializedWrites && record.RootElement.GetProperty("method").GetString() == "initialized"))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
        public async Task<int> NextIdAsync() => (await NextRecordAsync()).GetProperty("id").GetInt32();
        public async Task<JsonElement> NextRecordAsync()
        {
            var line = await _written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }
        public void Reply(string line) => _output.Lines.Writer.TryWrite(line).ShouldBeTrue();
        public void CloseOutput() => _output.Lines.Writer.TryComplete();
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _output.Lines.Writer.TryComplete();
            _errors.Lines.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChannelReaderText : TextReader
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await Lines.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }
}
