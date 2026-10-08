using CodexTrayApp.Core.Notifications;
using CodexTrayApp.ViewModels;
using Shouldly;

namespace CodexTrayApp.Tests;

public class TrayNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Alert_text_names_the_window_the_threshold_and_the_reset()
    {
        var alert = new ThresholdAlert("codex:primary", "Current window", 80, 86.4, Now.AddHours(1).AddMinutes(53));

        TrayIconViewModel.FormatAlert(alert, Now).ShouldBe("Current window usage is at 86%, past your 80% mark. Resets in 1h 53m.");
    }

    [Fact]
    public void Alert_text_copes_without_a_reset_time()
    {
        var alert = new ThresholdAlert("nimbus_quill", "Nimbus quill", 95, 100, null);

        TrayIconViewModel.FormatAlert(alert, Now).ShouldBe("Nimbus quill usage is at 100%, past your 95% mark.");
    }
}
