using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.History;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Storage;
using CodexTrayApp.Core.Tests.TestSupport;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class HistoryRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(DateTimeOffset updated) =>
        UsageSnapshot.Empty(updated) with { Windows = [UsageWindow.Create("codex:primary", 10, null)] };

    private static PollStatus Status(PollState state, UsageSnapshot? snapshot) =>
        new(state, snapshot, snapshot?.LastUpdated, Now, null, null, 0);

    [Fact]
    public void Records_fresh_ok_snapshots_once()
    {
        var store = Substitute.For<IHistoryStore>();
        var recorder = new HistoryRecorder(store, new HistoryOptions(), new FakeTimeProvider(Now), new ListLogger<HistoryRecorder>());
        var snapshot = Snapshot(Now);

        recorder.Record(Status(PollState.Ok, snapshot)).ShouldBeTrue();
        recorder.Record(Status(PollState.Ok, snapshot)).ShouldBeFalse();
        recorder.Record(Status(PollState.Stale, snapshot)).ShouldBeFalse();
        recorder.Record(Status(PollState.Ok, null)).ShouldBeFalse();
        recorder.Record(Status(PollState.Ok, Snapshot(Now.AddMinutes(5)))).ShouldBeTrue();

        store.Received(2).AppendSnapshot(Arg.Any<UsageSnapshot>());
    }

    [Fact]
    public void Prunes_at_most_once_a_day_using_the_retention_window()
    {
        var store = Substitute.For<IHistoryStore>();
        var clock = new FakeTimeProvider(Now);
        var recorder = new HistoryRecorder(store, new HistoryOptions { RetentionDays = 30 }, clock, new ListLogger<HistoryRecorder>());

        recorder.Record(Status(PollState.Ok, Snapshot(Now)));
        clock.Advance(TimeSpan.FromHours(1));
        recorder.Record(Status(PollState.Ok, Snapshot(clock.UtcNow)));
        clock.Advance(TimeSpan.FromHours(24));
        recorder.Record(Status(PollState.Ok, Snapshot(clock.UtcNow)));

        store.Received(1).Prune(Now.AddDays(-30));
        store.Received(1).Prune(clock.UtcNow.AddDays(-30));
        store.Received(2).Prune(Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public void A_failing_store_is_logged_not_thrown()
    {
        var store = Substitute.For<IHistoryStore>();
        store.When(s => s.AppendSnapshot(Arg.Any<UsageSnapshot>())).Do(_ => throw new InvalidOperationException("disk"));
        var log = new ListLogger<HistoryRecorder>();
        var recorder = new HistoryRecorder(store, new HistoryOptions(), new FakeTimeProvider(Now), log);

        recorder.Record(Status(PollState.Ok, Snapshot(Now))).ShouldBeFalse();
        log.All.ShouldContain("History could not be recorded");
    }
}
