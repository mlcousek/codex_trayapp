using System.Text;
using System.Text.Json;
using CodexTrayApp.Core.Analytics;
using CodexTrayApp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

/// <summary>Redacted fixtures use keys confirmed by tools/inspect-session-schema.py; no conversation content.</summary>
public sealed class SessionIngestionFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codex-fixtures-" + Guid.NewGuid().ToString("N"));
    private const string Meta = """{"type":"session_meta","payload":{"id":"fixture-session","session_id":"fixture-session","cwd":"C:/fixture","creator_account_id":"fixture-account","source":"cli"}}""";
    private const string FirstModel = """{"type":"turn_context","payload":{"model":"fixture-model-a","cwd":"C:/fixture"}}""";
    private const string SecondModel = """{"type":"turn_context","payload":{"model":"fixture-model-b","cwd":"C:/fixture"}}""";
    private static string Tokens(long input, long output, long cached = 0, long cacheWrite = 0) => JsonSerializer.Serialize(new
    {
        type = "event_msg", timestamp = "2026-10-08T08:00:00Z", payload = new
        {
            type = "token_count", info = new
            {
                total_token_usage = new { input_tokens = input, output_tokens = output, cached_input_tokens = cached, cache_write_input_tokens = cacheWrite, reasoning_output_tokens = output, total_tokens = input + output },
                last_token_usage = new { input_tokens = 999, output_tokens = 999, reasoning_output_tokens = 999 }
            }
        }
    });
    private static UsageEvent? Parse(CodexSessionContext context, string line) => context.Parse(Encoding.UTF8.GetBytes(line));
    private static CodexSessionContext Start()
    {
        var context = new CodexSessionContext(); Parse(context, Meta); Parse(context, FirstModel); return context;
    }
    private JsonlScanner Scanner(SqliteStore store) => new(_root, store, TimeProvider.System, NullLogger<JsonlScanner>.Instance);
    private string Write(string folder, string name, string text)
    {
        var path = Path.Combine(_root, folder, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }

    [Fact]
    public void Model_changes_attribute_only_new_cumulative_delta_and_ignore_last_usage()
    {
        var context = Start();
        var first = Parse(context, Tokens(100, 10, 40, 5))!;
        Parse(context, SecondModel);
        Parse(context, Tokens(100, 10, 40, 5)).ShouldBeNull();
        var second = Parse(context, Tokens(150, 15, 60, 8))!;
        first.Model.ShouldBe("fixture-model-a"); second.Model.ShouldBe("fixture-model-b");
        first.TotalTokens.ShouldBe(115); second.TotalTokens.ShouldBe(58);
        second.InputTokens.ShouldBe(30); second.CacheReadTokens.ShouldBe(20); second.CacheWrite5mTokens.ShouldBe(3);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"100\"")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public void Invalid_counters_leave_baseline_untouched(string invalid)
    {
        var context = Start(); Parse(context, Tokens(100, 10));
        var malformed = Tokens(150, 15).Replace("\"input_tokens\":150", "\"input_tokens\":" + invalid, StringComparison.Ordinal);
        Parse(context, malformed).ShouldBeNull(); context.Input.ShouldBe(100); context.Output.ShouldBe(10);
        Parse(context, Tokens(150, 15))!.TotalTokens.ShouldBe(55);
    }

    [Fact]
    public void Missing_required_counter_and_malformed_metadata_are_ignored()
    {
        var context = Start(); Parse(context, Tokens(100, 10));
        Parse(context, Tokens(150, 15).Replace("\"input_tokens\":150,", "", StringComparison.Ordinal)).ShouldBeNull();
        Parse(context, "{partial").ShouldBeNull(); Parse(context, "[]").ShouldBeNull();
        Parse(context, """{"type":"event_msg","payload":{"type":"token_count","info":null}}""").ShouldBeNull();
        Parse(context, Tokens(150, 15))!.TotalTokens.ShouldBe(55);
    }

    [Fact]
    public void Resume_context_serialization_preserves_model_baseline_and_reset_epoch_identity()
    {
        var context = Start(); var original = Parse(context, Tokens(100, 10))!;
        Parse(context, Tokens(20, 2)).ShouldBeNull();
        context = JsonSerializer.Deserialize<CodexSessionContext>(JsonSerializer.Serialize(context))!;
        var next = Parse(context, Tokens(100, 10))!;
        next.MessageId.ShouldNotBe(original.MessageId); next.TotalTokens.ShouldBe(88); next.Model.ShouldBe(original.Model);
        Parse(context, Tokens(100, 10)).ShouldBeNull();
        Parse(context, Meta); Parse(context, Tokens(110, 11))!.TotalTokens.ShouldBe(11);
    }

    [Fact]
    public void Different_session_metadata_resets_counters_and_model()
    {
        var context = Start(); Parse(context, Tokens(100, 10));
        Parse(context, Meta.Replace("fixture-session", "another-session", StringComparison.Ordinal));
        var next = Parse(context, Tokens(20, 2))!;
        next.TotalTokens.ShouldBe(22); next.Model.ShouldBe("unknown"); next.SessionId.ShouldBe("another-session");
    }

    [Fact]
    public void Active_archive_copies_restart_and_truncation_preserve_totals_and_source_bytes()
    {
        var prefix = string.Join("\r\n", Meta, FirstModel, Tokens(100, 10, 40), Tokens(100, 10, 40), SecondModel, Tokens(150, 15, 60)) + "\r\n";
        var active = Write("sessions", "sample.jsonl", prefix);
        var archived = Write("archived_sessions", "copy.jsonl", prefix);
        var store = new SqliteStore(Path.Combine(_root, "history.db"));
        Scanner(store).Scan(CancellationToken.None).NewEvents.ShouldBe(2);
        Scanner(new SqliteStore(store.DatabasePath)).Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        var totals = store.TotalsByModel(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        totals.Single(x => x.Model == "fixture-model-a").Tokens.Total.ShouldBe(110);
        totals.Single(x => x.Model == "fixture-model-b").Tokens.Total.ShouldBe(55);
        File.ReadAllText(active).ShouldBe(prefix); File.ReadAllText(archived).ShouldBe(prefix);
        // Truncated source is replayed; persisted cumulative identities suppress its old prefix.
        var shortened = string.Join("\n", Meta, FirstModel, Tokens(100, 10, 40)) + "\n";
        File.WriteAllText(active, shortened);
        Scanner(store).Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        File.AppendAllText(active, SecondModel + "\n" + Tokens(150, 15, 60) + "\n" + Tokens(200, 20, 80) + "\n");
        Scanner(store).Scan(CancellationToken.None).NewEvents.ShouldBe(1);
        store.TotalsByModel(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Sum(x => x.Tokens.Total).ShouldBe(220);
        File.ReadAllText(archived).ShouldBe(prefix);
    }

    [Fact]
    public void Archive_move_and_partial_resume_do_not_reimport_completed_prefix()
    {
        var prefix = Meta + "\n" + FirstModel + "\n" + Tokens(100, 10) + "\n";
        var path = Write("sessions", "sample.jsonl", prefix);
        var store = new SqliteStore(Path.Combine(_root, "history.db"));
        Scanner(store).Scan(CancellationToken.None).NewEvents.ShouldBe(1);
        var next = Tokens(150, 15); File.AppendAllText(path, next[..20]);
        Scanner(store).Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        File.AppendAllText(path, next[20..] + "\n");
        Scanner(new SqliteStore(store.DatabasePath)).Scan(CancellationToken.None).NewEvents.ShouldBe(1);
        Directory.CreateDirectory(Path.Combine(_root, "archived_sessions"));
        File.Move(path, Path.Combine(_root, "archived_sessions", "sample.jsonl"));
        Scanner(store).Scan(CancellationToken.None).NewEvents.ShouldBe(0);
        store.TotalsByModel(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Single().Tokens.Total.ShouldBe(165);
    }

    [Fact]
    public void Forked_sessions_keep_independent_cumulative_baselines_across_restart()
    {
        var parent = Write("sessions", "parent.jsonl", Meta + "\n" + FirstModel + "\n" + Tokens(100, 10) + "\n");
        var forkMetadata = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "fork-session", forked_from_id = "fixture-session", cwd = "C:/fixture" } });
        var fork = Write("sessions", "fork.jsonl", forkMetadata + "\n" + FirstModel + "\n" + Tokens(20, 2) + "\n");
        var parentSource = File.ReadAllText(parent);
        var forkSource = File.ReadAllText(fork);
        var store = new SqliteStore(Path.Combine(_root, "fork-history.db"));

        Scanner(store).Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(2);
        Scanner(new SqliteStore(store.DatabasePath)).Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(0);
        store.TotalsByModel(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Single().Tokens.Total.ShouldBe(132);
        File.ReadAllText(parent).ShouldBe(parentSource);
        File.ReadAllText(fork).ShouldBe(forkSource);
    }

    [Fact]
    public void Prompt_and_account_metadata_are_not_written_to_derived_storage()
    {
        const string PromptSentinel = "SYNTHETIC_PROMPT_SENTINEL";
        const string AccountSentinel = "SYNTHETIC_ACCOUNT_SENTINEL";
        const string CredentialSentinel = "SYNTHETIC_CREDENTIAL_SENTINEL";
        var metadata = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new { id = "privacy-fixture", cwd = "C:/fixture", creator_account_id = AccountSentinel, base_instructions = PromptSentinel, access_token = CredentialSentinel },
        });
        var source = Write("sessions", "privacy.jsonl", metadata + "\n" + FirstModel + "\n" + Tokens(100, 10) + "\n");
        var original = File.ReadAllText(source);
        var storePath = Path.Combine(_root, "privacy.db");
        var store = new SqliteStore(storePath);

        Scanner(store).Scan(TestContext.Current.CancellationToken).NewEvents.ShouldBe(1);
        var context = store.GetScanState(source)!.Context!;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        context.ShouldNotContain(PromptSentinel);
        context.ShouldNotContain(AccountSentinel);
        context.ShouldNotContain(CredentialSentinel);
        Encoding.Latin1.GetString(File.ReadAllBytes(storePath)).ShouldNotContain(PromptSentinel);
        Encoding.Latin1.GetString(File.ReadAllBytes(storePath)).ShouldNotContain(AccountSentinel);
        Encoding.Latin1.GetString(File.ReadAllBytes(storePath)).ShouldNotContain(CredentialSentinel);
        File.ReadAllText(source).ShouldBe(original);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
