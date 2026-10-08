using CodexTrayApp.Core.Diagnostics;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(44, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(150, "3 min ago")]
    [InlineData(3540, "59 min ago")]
    [InlineData(3600, "1 h ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(84000, "23 h ago")]
    [InlineData(90000, "yesterday")]
    [InlineData(259200, "3 days ago")]
    public void Formats_coarsely(int secondsAgo, string expected) =>
        RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now).ShouldBe(expected);
}
