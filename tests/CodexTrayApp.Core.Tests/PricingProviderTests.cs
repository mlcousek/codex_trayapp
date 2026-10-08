using CodexTrayApp.Core.Pricing;
using CodexTrayApp.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class PricingProviderTests : IDisposable
{
    private const string Bundled = """
        {"effectiveDate":"2026-06-24","currency":"USD","models":[{"id":"codex-a","input":1,"output":2}]}
        """;

    private const string Override = """
        {"effectiveDate":"2026-09-01","currency":"USD","models":[{"id":"codex-a","input":3,"output":4},{"id":"codex-b","input":5,"output":6}]}
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    public PricingProviderTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(BundledPath, Bundled);
        File.WriteAllText(OverridePath, Override);
    }

    private string BundledPath => Path.Combine(_directory, "pricing.json");

    private string OverridePath => Path.Combine(_directory, "custom.json");

    [Fact]
    public void Loads_the_bundled_file_lazily()
    {
        var provider = new PricingProvider(BundledPath, new ListLogger<PricingProvider>());

        provider.Current.SourcePath.ShouldBe(BundledPath);
        provider.Current.Models.Count.ShouldBe(1);
        provider.OverridePath.ShouldBeNull();
        provider.OverrideFailed.ShouldBeFalse();
    }

    [Fact]
    public void Reload_switches_to_an_override_and_back()
    {
        var provider = new PricingProvider(BundledPath, new ListLogger<PricingProvider>());
        var raised = 0;
        provider.Changed += (_, _) => raised++;

        provider.Reload(OverridePath).ShouldBeTrue();
        provider.Current.SourcePath.ShouldBe(OverridePath);
        provider.Current.Models.Count.ShouldBe(2);
        provider.Current.EffectiveDate.ShouldBe(new DateOnly(2026, 9, 1));
        provider.OverrideFailed.ShouldBeFalse();

        provider.Reload(OverridePath).ShouldBeFalse();
        provider.Reload(" ").ShouldBeTrue();
        provider.Current.SourcePath.ShouldBe(BundledPath);
        raised.ShouldBe(2);
    }

    [Fact]
    public void A_missing_override_falls_back_and_is_flagged()
    {
        var provider = new PricingProvider(BundledPath, new ListLogger<PricingProvider>());
        _ = provider.Current;

        provider.Reload(Path.Combine(_directory, "missing.json")).ShouldBeFalse();

        provider.Current.SourcePath.ShouldBe(BundledPath);
        provider.OverrideFailed.ShouldBeTrue();
    }

    [Fact]
    public void Bundled_price_matches_the_dated_standard_api_rate_and_unknown_models_stay_unknown()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "PricingFixture", "pricing.json");
        var table = PricingLoader.Load(null, path, NullLogger<PricingProvider>.Instance);

        table.EffectiveDate.ShouldBe(new DateOnly(2026, 10, 8));
        table.Currency.ShouldBe("USD");
        table.Cost("gpt-5.3-codex", new(100_000, 20_000, 0, 0, 300_000, 1)).ShouldBe(0.5075m);
        table.Cost("future-codex-model", new(100_000, 20_000, 0, 0, 300_000, 1)).ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
