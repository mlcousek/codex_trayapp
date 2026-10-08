using CodexTrayApp.Core.Domain;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class EpochTimeTests
{
    [Fact]
    public void A_typical_seconds_scale_value_is_read_as_seconds()
    {
        const long value = 1_700_000_000L; // 2023-11-14, far below the threshold

        EpochTime.FromUnixSecondsOrMilliseconds(value).ShouldBe(DateTimeOffset.FromUnixTimeSeconds(value));
    }

    [Fact]
    public void The_same_real_moment_expressed_in_milliseconds_is_read_as_milliseconds()
    {
        const long value = 1_700_000_000_000L; // same moment as above, times 1000

        EpochTime.FromUnixSecondsOrMilliseconds(value).ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(value));
    }

    [Fact]
    public void The_boundary_value_itself_is_read_as_milliseconds()
    {
        const long boundary = 100_000_000_000L; // 10^11, the documented switch-over point

        EpochTime.FromUnixSecondsOrMilliseconds(boundary).ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(boundary));
    }

    [Fact]
    public void One_below_the_boundary_is_still_read_as_seconds()
    {
        const long justBelow = 99_999_999_999L;

        EpochTime.FromUnixSecondsOrMilliseconds(justBelow).ShouldBe(DateTimeOffset.FromUnixTimeSeconds(justBelow));
    }

    [Fact]
    public void Zero_is_read_as_the_unix_epoch_in_seconds()
    {
        EpochTime.FromUnixSecondsOrMilliseconds(0).ShouldBe(DateTimeOffset.UnixEpoch);
    }
}
