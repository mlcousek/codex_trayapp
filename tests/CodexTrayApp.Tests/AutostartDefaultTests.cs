using CodexTrayApp.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>
/// The start-with-Windows default, against a fake registry. Nothing here reaches HKCU: the real Run entry belongs to
/// whoever is running the tests, and a test suite has no business creating startup entries on their machine.
/// </summary>
public class AutostartDefaultTests
{
    [Fact]
    public void The_entry_is_created_on_the_first_run()
    {
        var entry = new FakeAutostartEntry();

        Apply(entry).ShouldBe(AutostartDefaultOutcome.Enabled);

        entry.Enabled.ShouldBeTrue();
        entry.Marked.ShouldBeTrue();
        entry.SetCalls.ShouldBe(1);
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        var entry = new FakeAutostartEntry();

        Apply(entry);
        Apply(entry).ShouldBe(AutostartDefaultOutcome.AlreadyDecided);

        entry.SetCalls.ShouldBe(1);
    }

    [Fact]
    public void Switching_it_off_afterwards_survives_the_next_launch()
    {
        var entry = new FakeAutostartEntry();
        Apply(entry);

        entry.TrySet(false, out _);

        Apply(entry).ShouldBe(AutostartDefaultOutcome.AlreadyDecided);
        entry.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void An_entry_that_is_already_there_is_left_alone_and_recorded()
    {
        var entry = new FakeAutostartEntry { Enabled = true };

        Apply(entry).ShouldBe(AutostartDefaultOutcome.AlreadyEnabled);

        entry.SetCalls.ShouldBe(0);
        entry.Marked.ShouldBeTrue();
    }

    [Fact]
    public void A_refused_write_is_not_recorded_so_the_next_launch_tries_again()
    {
        var entry = new FakeAutostartEntry { Refuse = "Windows refused the startup entry" };

        Apply(entry).ShouldBe(AutostartDefaultOutcome.Refused);

        entry.Marked.ShouldBeFalse();
        entry.Enabled.ShouldBeFalse();

        entry.Refuse = null;
        Apply(entry).ShouldBe(AutostartDefaultOutcome.Enabled);
        entry.Enabled.ShouldBeTrue();
    }

    private static AutostartDefaultOutcome Apply(IAutostartEntry entry) =>
        new AutostartDefault(entry, NullLogger<AutostartDefault>.Instance).Apply();

    private sealed class FakeAutostartEntry : IAutostartEntry
    {
        public bool Enabled { get; set; }

        public bool Marked { get; private set; }

        public int SetCalls { get; private set; }

        /// <summary>When set, every write is refused with this reason, as a locked-down registry would.</summary>
        public string? Refuse { get; set; }

        public bool IsEnabled() => Enabled;

        public bool TrySet(bool enabled, out string? reason)
        {
            SetCalls++;
            if (Refuse is not null)
            {
                reason = Refuse;
                return false;
            }

            Enabled = enabled;
            reason = null;
            return true;
        }

        public bool WasDefaultApplied() => Marked;

        public void MarkDefaultApplied() => Marked = true;
    }
}
