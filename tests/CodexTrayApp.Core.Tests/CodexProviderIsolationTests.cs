using System.Text.Json;
using CodexTrayApp.Core.Account;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Tests.TestSupport;
using CodexTrayApp.Core.Codex;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class CodexProviderIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codex-provider-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("null", UsageFetchStatus.NoCredentials)]
    [InlineData("{\"type\":\"apiKey\"}", UsageFetchStatus.ApiKeyUnsupported)]
    [InlineData("{\"type\":\"amazonBedrock\"}", UsageFetchStatus.ApiKeyUnsupported)]
    public async Task Unsupported_or_signed_out_modes_clear_account_and_never_read_limits(string identity, UsageFetchStatus expected)
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("{\"account\":" + identity + "}"));
        var factory = Factory(client);
        using var provider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, factory);
        var changes = 0;
        provider.AccountChanged += (_, _) => changes++;

        (await provider.FetchAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(expected);
        (await provider.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(AccountInfo.Empty);
        changes.ShouldBe(2);
        await client.DidNotReceive().RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>());
        await client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Changed_identity_is_signaled_before_limits_and_survives_limits_failure()
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        var identity = "first@example.test";
        var failLimits = false;
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Json("{\"account\":{\"type\":\"chatgpt\",\"email\":\"" + identity + "\",\"planType\":\"plus\"}}")));
        client.RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(_ => failLimits ? Task.FromException<JsonElement>(new IOException("synthetic failure")) : Task.FromResult(Json("""{"accountId":"synthetic-a","rateLimits":{"primary":{"usedPercent":23}}}""")));
        using var provider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client));
        (await provider.FetchAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var changed = false;
        provider.AccountChanged += (_, _) => changed = true;
        identity = "second@example.test";
        failLimits = true;

        (await provider.FetchAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UsageFetchStatus.NetworkError);
        changed.ShouldBeTrue();
        (await provider.ReadAsync(TestContext.Current.CancellationToken)).Email.ShouldBe("second@example.test");
    }

    [Fact]
    public async Task Same_account_does_not_invalidate_usage_on_each_poll()
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"account":{"type":"chatgpt","email":"sample@example.test","planType":"plus"}}"""));
        client.RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"accountId":"synthetic-a","rateLimits":{"primary":{"usedPercent":23}}}"""));
        using var provider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client));
        await provider.FetchAsync(TestContext.Current.CancellationToken);
        var changes = 0;
        provider.AccountChanged += (_, _) => changes++;
        await provider.FetchAsync(TestContext.Current.CancellationToken);
        changes.ShouldBe(1); // Source confirmation is emitted each poll; consumers compare the fingerprint.
    }

    [Fact]
    public async Task Missing_account_email_uses_a_stable_home_identity_across_polls()
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"account":{"type":"chatgpt","planType":"plus"}}"""));
        client.RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"accountId":"synthetic-a","rateLimits":{"primary":{"usedPercent":23}}}"""));
        using var provider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client));
        var identityChanges = 0;
        provider.AccountChanged += (_, args) => identityChanges += args.AccountKey is null ? 0 : 1;

        var first = await provider.FetchAsync(TestContext.Current.CancellationToken);
        var second = await provider.FetchAsync(TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Snapshot!.AccountIdentityKey.ShouldBe(first.Snapshot!.AccountIdentityKey);
        second.Snapshot.AccountKey.ShouldBe(first.Snapshot.AccountKey);
        identityChanges.ShouldBe(1);
    }

    [Theory]
    [InlineData("account/read")]
    [InlineData("account/rateLimits/read")]
    public async Task Unsupported_account_method_reports_cli_compatibility_guidance(string method)
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Json("""{"account":{"type":"chatgpt","email":"sample@example.test"}}"""));
        client.RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Json("""{"rateLimits":{"primary":{"usedPercent":23}}}"""));
        client.RequestAsync(method, Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<JsonElement>(new CodexRpcException(-32601)));
        using var provider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client));

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.CliIncompatible);
        result.Message.ShouldNotBeNull().ShouldContain("Update the Codex CLI");
    }

    private static ICodexRpcClientFactory Factory(ICodexRpcClient client)
    {
        var factory = Substitute.For<ICodexRpcClientFactory>();
        factory.StartAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(client);
        return factory;
    }

    [Fact]
    public async Task Restored_cache_confirmed_by_same_account_read_survives_limits_failure()
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"account":{"type":"chatgpt","email":"sample@example.test","planType":"plus"}}"""));
        var failLimits = false;
        client.RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(_ => failLimits ? Task.FromException<JsonElement>(new IOException("offline")) : Task.FromResult(Json("""{"accountId":"synthetic-a","rateLimits":{"primary":{"usedPercent":23}}}""")));
        var cache = new InMemorySnapshotCache();
        using (var firstProvider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client)))
        using (var firstPoller = new UsagePoller(firstProvider, cache, new PollingOptions(), TimeProvider.System, new ListLogger<UsagePoller>()))
        {
            await firstPoller.FetchOnceAsync(TestContext.Current.CancellationToken);
        }

        failLimits = true;
        using var restoredProvider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client));
        using var restoredPoller = new UsagePoller(restoredProvider, cache, new PollingOptions(), TimeProvider.System, new ListLogger<UsagePoller>());
        await restoredPoller.InitialiseAsync(TestContext.Current.CancellationToken);
        await restoredPoller.FetchOnceAsync(TestContext.Current.CancellationToken);

        restoredPoller.Status.State.ShouldBe(PollState.Stale);
        restoredPoller.Status.Snapshot.ShouldNotBeNull().PrimaryWindow.ShouldNotBeNull().UtilizationPercent.ShouldBe(23);
        cache.Stored.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_with_a_different_home_clears_cache_even_when_connection_fails(bool differentHome)
    {
        using var settings = Settings();
        var client = Substitute.For<ICodexRpcClient>();
        client.RequestAsync("account/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"account":{"type":"chatgpt","email":"sample@example.test","planType":"plus"}}"""));
        client.RequestAsync("account/rateLimits/read", Arg.Any<object?>(), Arg.Any<CancellationToken>()).Returns(Json("""{"accountId":"synthetic-a","rateLimits":{"primary":{"usedPercent":23}}}"""));
        var cache = new InMemorySnapshotCache();
        using (var firstProvider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, Factory(client)))
        using (var firstPoller = new UsagePoller(firstProvider, cache, new PollingOptions(), TimeProvider.System, new ListLogger<UsagePoller>()))
        {
            await firstPoller.FetchOnceAsync(TestContext.Current.CancellationToken);
        }
        if (differentHome) { settings.Save(settings.Current with { CodexHomePath = Path.Combine(_root, "other-home") }); }
        var offlineFactory = Substitute.For<ICodexRpcClientFactory>();
        offlineFactory.StartAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ICodexRpcClient>(new IOException("offline")));
        using var restoredProvider = new CodexUsageProvider(Paths(), settings, NullLogger<CodexUsageProvider>.Instance, offlineFactory);
        using var restoredPoller = new UsagePoller(restoredProvider, cache, new PollingOptions(), TimeProvider.System, new ListLogger<UsagePoller>());
        await restoredPoller.InitialiseAsync(TestContext.Current.CancellationToken);
        await restoredPoller.FetchOnceAsync(TestContext.Current.CancellationToken);

        restoredPoller.Status.State.ShouldBe(PollState.Stale);
        if (differentHome)
        {
            restoredPoller.Status.Snapshot.ShouldBeNull();
            cache.Stored.ShouldBeNull();
        }
        else
        {
            restoredPoller.Status.Snapshot.ShouldNotBeNull();
            cache.Stored.ShouldNotBeNull();
        }
    }
    private AppPaths Paths() => new(_root, _root, _root);
    private SettingsStore Settings()
    {
        var settings = new SettingsStore(Path.Combine(_root, "settings.json"), NullLogger<SettingsStore>.Instance);
        settings.Save(AppSettings.Default with { CodexExecutablePath = Environment.ProcessPath });
        return settings;
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
