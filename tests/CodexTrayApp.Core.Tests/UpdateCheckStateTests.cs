using CodexTrayApp.Core.Tests.TestSupport;
using CodexTrayApp.Core.Updates;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public sealed class UpdateCheckStateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexTrayApp.Tests", Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_directory, "update-check.json");

    [Fact]
    public void A_check_is_due_when_it_has_never_run()
    {
        UpdateCheckState.Empty.IsDue(Now, Week).ShouldBeTrue();
        UpdateCheckState.Empty.LastNotifiedVersion.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(30, true)]
    public void A_check_is_due_once_the_interval_has_elapsed(int daysAgo, bool expected) =>
        new UpdateCheckState(Now.AddDays(-daysAgo), "0.1.1").IsDue(Now, Week).ShouldBe(expected);

    [Fact]
    public void A_clock_that_moved_back_does_not_wedge_the_check_shut()
    {
        // A stale future timestamp (a clock correction, a restored profile) would otherwise block checks forever.
        new UpdateCheckState(Now.AddDays(3), "0.1.1").IsDue(Now, Week).ShouldBeTrue();
    }

    [Fact]
    public void State_survives_a_round_trip()
    {
        var store = Create();
        var state = new UpdateCheckState(Now, "0.2.0");

        store.Save(state);

        var reloaded = Create().Load();
        reloaded.LastCheckedUtc.ShouldBe(Now);
        reloaded.LastNotifiedVersion.ShouldBe("0.2.0");
        File.ReadAllText(StatePath).ShouldContain("lastNotifiedVersion");
    }

    [Fact]
    public void A_missing_file_reads_as_never_checked() => Create().Load().ShouldBe(UpdateCheckState.Empty);

    [Fact]
    public void A_corrupt_file_reads_as_never_checked_and_is_left_alone()
    {
        Directory.CreateDirectory(_directory);
        const string Broken = "{ \"lastCheckedUtc\": ";
        File.WriteAllText(StatePath, Broken);

        Create().Load().ShouldBe(UpdateCheckState.Empty);

        File.ReadAllText(StatePath).ShouldBe(Broken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private UpdateCheckStateStore Create() => new(StatePath, new ListLogger<UpdateCheckStateStore>());
}
