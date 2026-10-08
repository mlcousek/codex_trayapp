using System.Text.Json;
using CodexTrayApp.Core.Codex;
using CodexTrayApp.Core.Domain;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class CodexUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement Account => Json("""{"type":"chatgpt","email":"sample@example.test","planType":"plus"}""");

    [Fact]
    public void Multi_bucket_view_replaces_legacy_without_duplicates()
    {
        var limits = Json("""{"accountId":"synthetic-account","rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":23,"windowDurationMins":300,"resetsAt":1791446400},"secondary":{"usedPercent":4,"windowDurationMins":10080}},"other":{"limitName":"Reserve","primary":{"usedPercent":0,"windowDurationMins":15}}}}""");
        var snapshot = CodexUsageParser.Parse(limits, Account, Now);
        snapshot.Windows.Count.ShouldBe(3);
        snapshot.PrimaryWindow!.UtilizationPercent.ShouldBe(23);
        snapshot.PrimaryWindow.DurationMinutes.ShouldBe(300);
        snapshot.Windows[1].ResetsAt.ShouldBeNull();
        snapshot.Windows[2].DisplayName.ShouldBe("Reserve · 15 min");
        snapshot.AccountKey.ShouldNotBeNull().ShouldNotContain("synthetic-account");
    }

    [Fact]
    public void Null_optional_fields_do_not_fabricate_a_reset_or_duration()
    {
        var snapshot = CodexUsageParser.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":null,"resetsAt":null},"secondary":null}}"""), Account, Now);
        snapshot.Windows.Count.ShouldBe(1);
        snapshot.PrimaryWindow!.ResetsAt.ShouldBeNull();
        snapshot.PrimaryWindow.DurationMinutes.ShouldBeNull();
        snapshot.PrimaryWindow.DisplayName.ShouldBe("Current window");
        snapshot.WindowFor("missing").ShouldBeNull();
    }

    [Fact]
    public void Credits_are_not_converted_to_money()
    {
        var snapshot = CodexUsageParser.Parse(Json("""{"rateLimits":{"credits":{"unlimited":false,"balance":"240.5","hasCredits":true},"primary":{"usedPercent":3}},"rateLimitResetCredits":{"availableCount":2,"credits":[]}}"""), Account, Now);
        snapshot.CreditsText.ShouldBe("240.5 credits available · 2 earned resets");
        snapshot.Overage.ShouldBeNull();
    }

    [Fact]
    public void Account_key_is_stable_and_changes_with_account()
    {
        var a = CodexUsageParser.Parse(Json("""{"rateLimits":{}}"""), Account, Now);
        var b = CodexUsageParser.Parse(Json("""{"rateLimits":{}}"""), Json("""{"email":"another@example.test"}"""), Now);
        a.AccountKey.ShouldNotBe(b.AccountKey);
        a.AccountKey.ShouldBe(CodexUsageParser.Parse(Json("""{"rateLimits":{}}"""), Account, Now.AddHours(1)).AccountKey);
    }

    [Fact]
    public void Empty_multi_bucket_map_falls_back_to_legacy()
    {
        var snapshot = CodexUsageParser.Parse(Json("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":17}}}"""), Account, Now);
        snapshot.Windows.ShouldHaveSingleItem().UtilizationPercent.ShouldBe(17);
    }

    [Fact]
    public void Malformed_optional_fields_and_buckets_do_not_hide_valid_windows()
    {
        var snapshot = CodexUsageParser.Parse(Json("""{"rateLimitsByLimitId":{"invalid":null,"codex":{"primary":{"usedPercent":"unknown"},"secondary":{"usedPercent":19,"resetsAt":"unknown","windowDurationMins":[]}}},"rateLimitResetCredits":{"availableCount":"unknown"}}"""), Account, Now);
        var window = snapshot.Windows.ShouldHaveSingleItem();
        window.Key.ShouldBe("codex:secondary");
        window.UtilizationPercent.ShouldBe(19);
        window.ResetsAt.ShouldBeNull();
        window.DurationMinutes.ShouldBeNull();
        snapshot.CreditsText.ShouldBeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("17")]
    public void Non_object_limits_raise_a_schema_error(string value)
    {
        Should.Throw<JsonException>(() => CodexUsageParser.Parse(Json(value), Account, Now));
    }
}
