using CodexTrayApp.Core.Domain;
using CodexTrayApp.Tray;
using Shouldly;

namespace CodexTrayApp.Tests;

public class TrayIconStateTests
{
    [Theory]
    [InlineData(null, "?")]
    [InlineData(0.0, "0")]
    [InlineData(12.4, "12")]
    [InlineData(86.6, "87")]
    [InlineData(99.4, "99")]
    [InlineData(99.5, "!")]
    [InlineData(100.0, "!")]
    public void Numeral_is_compact(double? percent, string expected) =>
        new TrayIconState(percent, UsageWindowStatus.Ok, false).Text.ShouldBe(expected);

    [Fact]
    public void Unknown_has_no_percent_and_is_not_stale()
    {
        TrayIconState.Unknown.Percent.ShouldBeNull();
        TrayIconState.Unknown.IsStale.ShouldBeFalse();
        TrayIconState.Unknown.Text.ShouldBe("?");
    }
}
