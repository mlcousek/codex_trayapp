using System.Text;
using System.Text.Json;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class CodexSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codex-test-" + Guid.NewGuid().ToString("N"));
    private const string Meta = """{"type":"session_meta","payload":{"id":"synthetic-session","cwd":"C:/sample"}}""";
    private const string Context = """{"type":"turn_context","payload":{"model":"synthetic-model"}}""";
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Tokens(long input, long output, long cached = 0) => JsonSerializer.Serialize(new { type = "event_msg", timestamp = "2026-10-08T08:00:00Z", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, output_tokens = output, cached_input_tokens = cached, reasoning_output_tokens = output } } } });

    [Fact]
    public void Cumulative_repeats_cached_input_and_reasoning_do_not_double_count()
    {
        var parser = new CodexSessionContext();
        parser.Parse(Bytes(Meta)); parser.Parse(Bytes(Context));
        var first = parser.Parse(Bytes(Tokens(100, 10, 40)))!;
        var second = parser.Parse(Bytes(Tokens(150, 15, 60)))!;
        parser.Parse(Bytes(Tokens(150, 15, 60))).ShouldBeNull();
        (first.TotalTokens + second.TotalTokens).ShouldBe(165);
        first.InputTokens.ShouldBe(60); first.CacheReadTokens.ShouldBe(40);
        second.InputTokens.ShouldBe(30); second.CacheReadTokens.ShouldBe(20);
        second.OutputTokens.ShouldBe(5);
    }

    [Fact]
    public void Scanner_survives_restart_and_partial_line_without_reimport_after_clear()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        var path = Path.Combine(_root, "sessions", "sample.jsonl");
        File.WriteAllText(path, Meta + "\n" + Context + "\n" + Tokens(100, 10) + "\n");
        var store = new SqliteStore(Path.Combine(_root, "history.db"));
        JsonlScanner Scanner() => new(_root, store, TimeProvider.System, NullLogger<JsonlScanner>.Instance);
        Scanner().Scan(CancellationToken.None).NewEvents.ShouldBe(1);
        Scanner().Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        var next = Tokens(150, 15);
        File.AppendAllText(path, next[..20]);
        Scanner().Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        File.AppendAllText(path, next[20..] + "\n");
        Scanner().Scan(CancellationToken.None).NewEvents.ShouldBe(1);
        var totals = store.TotalsByModel(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        totals.Single().Tokens.Total.ShouldBe(165);
        store.Clear();
        Scanner().Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        store.CountEvents().ShouldBe(0);
        File.AppendAllText(path, Tokens(200, 20) + "\n");
        Scanner().Scan(CancellationToken.None).NewEvents.ShouldBe(1);
        store.TotalsByModel(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Single().Tokens.Total.ShouldBe(55);
    }

    [Fact]
    public void Counter_drop_sets_baseline_instead_of_counting_old_activity()
    {
        var parser = new CodexSessionContext(); parser.Parse(Bytes(Meta));
        parser.Parse(Bytes(Tokens(100, 10))).ShouldNotBeNull();
        parser.Parse(Bytes(Tokens(20, 2))).ShouldBeNull();
        parser.Parse(Bytes(Tokens(30, 3)))!.TotalTokens.ShouldBe(11);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
