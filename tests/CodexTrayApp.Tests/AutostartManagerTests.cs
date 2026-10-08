using CodexTrayApp.Startup;
using Shouldly;

namespace CodexTrayApp.Tests;

public class AutostartManagerTests
{
    private const string Exe = @"C:\Tools\Codex Usage Tray\CodexTrayApp.exe";

    [Fact]
    public void The_run_command_quotes_the_executable() =>
        AutostartManager.BuildCommand(Exe).ShouldBe("\"" + Exe + "\"");

    [Theory]
    [InlineData("\"C:\\Tools\\Codex Usage Tray\\CodexTrayApp.exe\"", true)]
    [InlineData("\"c:\\tools\\codex usage tray\\codextrayapp.exe\" --minimized", true)]
    [InlineData("  \"C:\\Tools\\Codex Usage Tray\\CodexTrayApp.exe\"  ", true)]
    [InlineData("\"C:\\Other\\CodexTrayApp.exe\"", false)]
    [InlineData("C:\\Other\\CodexTrayApp.exe --flag", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_stored_value_matches_only_this_executable(string? stored, bool expected) =>
        AutostartManager.Matches(stored, Exe).ShouldBe(expected);

    [Fact]
    public void An_unquoted_path_without_spaces_matches()
    {
        const string Plain = @"C:\Tools\CodexTrayApp.exe";

        AutostartManager.Matches(Plain + " --something", Plain).ShouldBeTrue();
        AutostartManager.Matches(Plain, Plain).ShouldBeTrue();
    }
}
