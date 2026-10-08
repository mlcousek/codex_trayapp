using System.IO;
using CodexTrayApp.Core;
using CodexTrayApp.Core.Cache;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Notifications;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Settings;
using CodexTrayApp.Startup;
using CodexTrayApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodexTrayApp.Tests;

public class TrayIconViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A clock that never moves, so reset countdowns and threshold text come out exactly as expected.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static UsageSnapshot Snapshot() => UsageSnapshot.Empty(Now) with
    {
        AccountKey = "synthetic-account",
        AccountIdentityKey = "synthetic-identity",
        AccountSourceKey = "synthetic-source",
        Windows =
        [
            UsageWindow.Create("codex:primary", 86, Now.AddHours(1).AddMinutes(53)),
            UsageWindow.Create("codex:secondary", 9.4, Now.AddDays(4)),
            UsageWindow.Create("nimbus_quill", 0, null),
            UsageWindow.Create("extra_window", 50, null),
        ],
    };

    private static PollStatus Status(PollState state) =>
        new(state, Snapshot(), Now, Now, Now.AddMinutes(5), null, 0);

    [Fact]
    public void Tooltip_lists_the_first_three_visible_windows_with_one_countdown()
    {
        TrayIconViewModel.BuildTooltip(Status(PollState.Ok), Now)
            .ShouldBe("Codex usage: Current window 86% (resets in 1h 53m) · Secondary window 9% · Extra window 50%");
    }

    [Fact]
    public void Tooltip_includes_inactive_codename_windows_only_when_asked()
    {
        TrayIconViewModel.BuildTooltip(Status(PollState.Ok), Now, showInactiveWindows: true)
            .ShouldBe("Codex usage: Current window 86% (resets in 1h 53m) · Secondary window 9% · Nimbus quill 0%");
    }

    [Theory]
    [InlineData(PollState.RateLimited, "rate limited")]
    [InlineData(PollState.Unauthenticated, "sign in with Codex CLI")]
    [InlineData(PollState.Stale, "stale")]
    [InlineData(PollState.Idle, "cached")]
    public void Tooltip_names_the_degraded_state(PollState state, string expected) =>
        TrayIconViewModel.BuildTooltip(Status(state), Now).ShouldEndWith(" · " + expected);

    [Fact]
    public void Tooltip_falls_back_to_the_status_message_without_data()
    {
        var status = new PollStatus(PollState.Unauthenticated, null, null, Now, null, "Sign in with Codex CLI first.", 0);

        TrayIconViewModel.BuildTooltip(status, Now).ShouldBe("Codex Usage Tray: Sign in with Codex CLI first.");
    }

    [Fact]
    public void Tooltip_never_exceeds_the_shell_limit()
    {
        var windows = Enumerable.Range(0, 3)
            .Select(i => UsageWindow.Create("a_very_long_window_key_number_" + i, 50, Now.AddDays(6)))
            .ToList();
        var status = new PollStatus(PollState.Ok, Snapshot() with { Windows = windows }, Now, Now, null, null, 0);

        var tooltip = TrayIconViewModel.BuildTooltip(status, Now);

        tooltip.Length.ShouldBeLessThanOrEqualTo(TrayIconViewModel.TooltipLimit);
        tooltip.ShouldEndWith("…");
    }

    /// <summary>
    /// Drives a real PollStatus through a fully constructed instance (real UsagePoller, SettingsStore pointed at a
    /// temp file, ThresholdNotifier and AutostartManager) rather than only the extracted static helpers. Never calls
    /// AutostartManager.TrySet - RefreshAutostart's IsEnabled() read is the only registry access, against a bogus
    /// executable path that will never match a real Run entry.
    /// </summary>
    [Fact]
    public void Apply_projects_icon_state_and_notifies_once_when_a_threshold_is_crossed()
    {
        StaThread.Run(() =>
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), "CodexTrayAppTests", Guid.NewGuid() + ".settings.json");
            using var settingsStore = new SettingsStore(settingsPath, NullLogger<SettingsStore>.Instance);
            try
            {
                settingsStore.Load();
                settingsStore.Save(AppSettings.Default with { Notifications = new NotificationSettings { Enabled = true, Thresholds = [80] } });

                using var poller = new UsagePoller(
                    Substitute.For<IUsageProvider>(),
                    Substitute.For<ISnapshotCache>(),
                    new PollingOptions(),
                    new FixedTimeProvider(Now),
                    NullLogger<UsagePoller>.Instance);

                var autostart = new AutostartManager(
                    Path.Combine(Path.GetTempPath(), "CodexTrayAppTests", "not-the-real-exe-" + Guid.NewGuid() + ".exe"),
                    NullLogger<AutostartManager>.Instance);

                var viewModel = new TrayIconViewModel(
                    poller,
                    new AppPaths(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath()),
                    settingsStore,
                    new ThresholdNotifier(),
                    autostart,
                    new FixedTimeProvider(Now),
                    System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    openSettings: () => { },
                    quit: () => { },
                    version: "0.0.0-test",
                    NullLogger<TrayIconViewModel>.Instance);
                try
                {
                    TrayNotification? notification = null;
                    viewModel.NotificationRequested += (_, n) => notification = n;

                    viewModel.Apply(Status(PollState.Ok));

                    viewModel.IconState.Percent.ShouldBe(86.0);
                    viewModel.IconState.Status.ShouldBe(UsageWindowStatus.Warning);
                    viewModel.IconState.IsStale.ShouldBeFalse();

                    notification.ShouldNotBeNull();
                    notification!.Title.ShouldBe("Codex usage");
                    notification.IsWarning.ShouldBeTrue();
                    notification.Message.ShouldBe("Current window usage is at 86%, past your 80% mark. Resets in 1h 53m.");

                    // The notifier remembers what it already fired for this reset period: applying the same status
                    // again must not raise a second notification.
                    notification = null;
                    viewModel.Apply(Status(PollState.Ok));
                    notification.ShouldBeNull();
                }
                finally
                {
                    viewModel.Dispose();
                }
            }
            finally
            {
                File.Delete(settingsPath);
            }

            return true;
        });
    }
}
