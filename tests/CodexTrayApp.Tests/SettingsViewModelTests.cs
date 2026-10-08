using CodexTrayApp.ViewModels;
using Shouldly;

namespace CodexTrayApp.Tests;

public class SettingsViewModelTests
{
    [Theory]
    [InlineData("300", true, 300, null)]
    [InlineData(" 600 ", true, 600, null)]
    [InlineData("10", true, 180, "floor")]
    [InlineData("999999", true, 86400, "Capped")]
    [InlineData("abc", false, 0, "whole number")]
    [InlineData("", false, 0, "whole number")]
    public void Poll_interval_is_parsed_and_clamped(string text, bool ok, int expected, string? noteContains)
    {
        SettingsViewModel.TryParseInterval(text, out var seconds, out var note).ShouldBe(ok);

        seconds.ShouldBe(expected);
        if (noteContains is null)
        {
            note.ShouldBeNull();
        }
        else
        {
            note.ShouldNotBeNull().ShouldContain(noteContains);
        }
    }

    [Theory]
    [InlineData("90", 90, null)]
    [InlineData("0", 1, "between")]
    [InlineData("5000", 3650, "between")]
    public void Retention_is_parsed_and_clamped(string text, int expected, string? noteContains)
    {
        SettingsViewModel.TryParseRetention(text, out var days, out var note).ShouldBeTrue();

        days.ShouldBe(expected);
        (note is null).ShouldBe(noteContains is null);
    }

    [Fact]
    public void Thresholds_accept_percent_signs_and_report_the_rest()
    {
        var values = SettingsViewModel.ParseThresholds("95%, 80, 80; 150 abc 0", out var note);

        values.ShouldBe([80, 95]);
        note.ShouldNotBeNull().ShouldContain("150, abc, 0");
    }

    [Fact]
    public void Empty_thresholds_are_allowed_but_explained()
    {
        SettingsViewModel.ParseThresholds("  ", out var note).ShouldBeEmpty();

        note.ShouldNotBeNull().ShouldContain("nothing will be announced");
    }

    [Fact]
    public void Choices_display_their_label()
    {
        SettingsViewModel.ChartRangeChoices.Select(c => c.Label).ShouldBe(["24 hours", "7 days", "30 days"]);
        SettingsViewModel.ThemeChoices[0].ToString().ShouldBe("Follow Windows");
    }
}
