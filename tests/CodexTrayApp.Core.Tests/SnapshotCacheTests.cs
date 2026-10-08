using CodexTrayApp.Core.Cache;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class SnapshotCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_directory, "nested", "cache.json");

    private SnapshotCache Cache() => new(CachePath, new ListLogger<SnapshotCache>());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Round_trips_a_snapshot_and_marks_it_as_cached()
    {
        var snapshot = new UsageSnapshot(
            [UsageWindow.Create("codex:primary", 51, Now.AddHours(2)), UsageWindow.Create("codex:secondary", 93.5, null)],
            "max",
            null,
            new OverageInfo(true, 12.5m, 50m, 25, "USD"),
            Now,
            UsageSource.Live) { AccountKey = "synthetic-a" };

        await Cache().SaveAsync(snapshot, TestContext.Current.CancellationToken);
        var loaded = await Cache().LoadAsync(TestContext.Current.CancellationToken);

        var result = loaded.ShouldNotBeNull();
        result.Source.ShouldBe(UsageSource.Cache);
        result.Windows.Count.ShouldBe(2);
        result.Windows[0].Key.ShouldBe("codex:primary");
        result.Windows[0].ResetsAt.ShouldBe(Now.AddHours(2));
        result.Windows[1].Status.ShouldBe(UsageWindowStatus.Critical);
        result.Overage.ShouldBe(snapshot.Overage);
        result.PlanTier.ShouldBe("max");
        result.LastUpdated.ShouldBe(Now);
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_cache_yet() =>
        (await Cache().LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

    [Fact]
    public async Task Survives_a_corrupt_cache_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, "{ not json", TestContext.Current.CancellationToken);

        (await Cache().LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Writes_atomically_leaving_no_temporary_file()
    {
        await Cache().SaveAsync(UsageSnapshot.Empty(Now), TestContext.Current.CancellationToken);

        File.Exists(CachePath).ShouldBeTrue();
        File.Exists(CachePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task The_cache_file_never_contains_a_token_field()
    {
        await Cache().SaveAsync(UsageSnapshot.Empty(Now) with { PlanTier = "max" }, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken);
        text.ShouldNotContain("token", Case.Insensitive);
    }

    [Fact]
    public async Task Account_partitions_preserve_their_own_snapshot_and_omit_email()
    {
        using var cache = Cache();
        var first = UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-a", AccountEmail = "private@example.test", PlanTier = "plus" };
        var second = UsageSnapshot.Empty(Now.AddMinutes(5)) with { AccountKey = "synthetic-b", AccountEmail = "second@example.test", PlanTier = "pro" };
        await cache.SaveAsync(first, TestContext.Current.CancellationToken);
        await cache.SaveAsync(second, TestContext.Current.CancellationToken);

        var active = (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        active.AccountKey.ShouldBe("synthetic-b");
        active.AccountEmail.ShouldBeNull();
        using var file = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken));
        file.RootElement.GetProperty("accounts").GetProperty("synthetic-a").GetProperty("planTier").GetString().ShouldBe("plus");
        var text = file.RootElement.GetRawText();
        text.ShouldNotContain("private@example.test");
        text.ShouldNotContain("second@example.test");
    }

    [Fact]
    public async Task Deactivated_or_unverified_account_does_not_restore_another_accounts_cache()
    {
        using var cache = Cache();
        await cache.SaveAsync(UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-a" }, TestContext.Current.CancellationToken);
        await cache.DeactivateAsync(TestContext.Current.CancellationToken);
        (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
        await cache.SaveAsync(UsageSnapshot.Empty(Now), TestContext.Current.CancellationToken);
        (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Legacy_unpartitioned_cache_is_not_restored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, """{"windows":[],"accountEmail":"old@example.test","source":"live"}""", TestContext.Current.CancellationToken);
        using var cache = Cache();
        (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_saves_keep_both_partitions_and_leave_no_temporary_files()
    {
        using var cache = Cache();
        await Task.WhenAll(
            cache.SaveAsync(UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-a" }, TestContext.Current.CancellationToken),
            cache.SaveAsync(UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-b" }, TestContext.Current.CancellationToken));
        using var file = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken));
        file.RootElement.GetProperty("accounts").EnumerateObject().Count().ShouldBe(2);
        Directory.GetFiles(Path.GetDirectoryName(CachePath)!, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":\"Quota\",\"utilizationPercent\":-1}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":\"Quota\",\"utilizationPercent\":101}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":\"Quota\",\"utilizationPercent\":1e1000}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":null,\"utilizationPercent\":30}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":\"Quota\",\"utilizationPercent\":30,\"status\":99}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":\"Quota\",\"utilizationPercent\":30,\"durationMinutes\":-1}]")]
    [InlineData("[{\"key\":\"quota\",\"displayName\":\"Quota\",\"utilizationPercent\":30},{\"key\":\"QUOTA\",\"displayName\":\"Quota\",\"utilizationPercent\":40}]")]
    public async Task Malformed_window_collections_are_not_restored(string windows)
    {
        var json = """{"version":1,"activeAccountKey":"synthetic-a","accounts":{"synthetic-a":{"accountKey":"synthetic-a","windows": """ + windows + """, "lastUpdated":"2026-09-07T08:00:00Z","source":"live"}}}""";
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, json, TestContext.Current.CancellationToken);
        using var cache = Cache();
        (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"synthetic-a\":null}")]
    [InlineData("{\"synthetic-a\":{\"accountKey\":\"synthetic-b\",\"windows\":[]}}")]
    [InlineData("{\"synthetic-a\":{\"accountKey\":\"synthetic-a\",\"windows\":[],\"source\":99}}")]
    public async Task Missing_or_malformed_account_partitions_are_not_restored(string accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, """{"version":1,"activeAccountKey":"synthetic-a","accounts": """ + accounts + "}", TestContext.Current.CancellationToken);
        using var cache = Cache();
        (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Valid_partition_survives_invalid_partition_and_is_sanitized_on_rewrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, """{"version":1,"activeAccountKey":"synthetic-a","accounts":{"invalid":null,"synthetic-a":{"accountKey":"synthetic-a","accountEmail":"private@example.test","windows":[{"key":"quota","displayName":"Quota","utilizationPercent":95,"status":"ok"}],"lastUpdated":"2026-09-07T08:00:00Z","source":"live"}}}""", TestContext.Current.CancellationToken);
        using var cache = Cache();
        var loaded = (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        loaded.AccountEmail.ShouldBeNull();
        loaded.Windows[0].Status.ShouldBe(UsageWindowStatus.Critical);
        await cache.SaveAsync(UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-b" }, TestContext.Current.CancellationToken);
        var file = await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken);
        file.ShouldNotContain("private@example.test");
        file.ShouldNotContain("invalid");
        file.ShouldContain("synthetic-a");
    }

    [Fact]
    public async Task Cancelled_save_preserves_previous_snapshot_and_leaves_no_temporary_files()
    {
        using var cache = Cache();
        await cache.SaveAsync(UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-a" }, TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => cache.SaveAsync(UsageSnapshot.Empty(Now) with { AccountKey = "synthetic-b" }, cancellation.Token));
        (await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken)).ShouldBe(before);
        Directory.GetFiles(Path.GetDirectoryName(CachePath)!, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"usedAmount\":-1}")]
    [InlineData("{\"limitAmount\":-1}")]
    [InlineData("{\"utilizationPercent\":101}")]
    [InlineData("{\"utilizationPercent\":1e1000}")]
    public async Task Invalid_overage_values_are_not_restored(string overage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, """{"version":1,"activeAccountKey":"synthetic-a","accounts":{"synthetic-a":{"accountKey":"synthetic-a","windows":[],"lastUpdated":"2026-09-07T08:00:00Z","overage": """ + overage + "}}}", TestContext.Current.CancellationToken);
        using var cache = Cache();
        (await cache.LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
