using System.Windows;
using CodexTrayApp.Views;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>
/// The flyout's size contract after a DPI change. On 2026-09-15 a move between a 200 % and a 100 % monitor left the
/// flyout 176 DIP wide (half its 352) and as tall as the work area, with SizeToContent silently switched to Manual, and
/// it stayed that way until the app restarted. These tests recreate that broken state on a plain window.
/// </summary>
public class FlyoutSizingTests
{
    private const double DesignWidth = 352;

    [Fact]
    public void The_state_a_dpi_change_leaves_behind_is_put_back()
    {
        var (restored, width, sizeToContent, heightIsLocal) = StaThread.Run(() =>
        {
            var window = new Window { Width = 176, Height = 828, SizeToContent = SizeToContent.Manual };

            var restored = FlyoutWindow.RestoreSizeToContent(window, DesignWidth);

            return (restored, window.Width, window.SizeToContent, window.ReadLocalValue(FrameworkElement.HeightProperty) != DependencyProperty.UnsetValue);
        });

        restored.ShouldBeTrue();
        width.ShouldBe(DesignWidth);
        sizeToContent.ShouldBe(SizeToContent.Height);
        heightIsLocal.ShouldBeFalse();
    }

    [Fact]
    public void A_rescaled_width_alone_is_enough_to_restore()
    {
        var (restored, width) = StaThread.Run(() =>
        {
            var window = new Window { Width = 176, SizeToContent = SizeToContent.Height };
            return (FlyoutWindow.RestoreSizeToContent(window, DesignWidth), window.Width);
        });

        restored.ShouldBeTrue();
        width.ShouldBe(DesignWidth);
    }

    [Fact]
    public void Lost_SizeToContent_alone_is_enough_to_restore()
    {
        var (restored, sizeToContent) = StaThread.Run(() =>
        {
            var window = new Window { Width = DesignWidth, Height = 900, SizeToContent = SizeToContent.Manual };
            return (FlyoutWindow.RestoreSizeToContent(window, DesignWidth), window.SizeToContent);
        });

        restored.ShouldBeTrue();
        sizeToContent.ShouldBe(SizeToContent.Height);
    }

    [Fact]
    public void A_healthy_window_is_left_alone() =>
        StaThread.Run(() => FlyoutWindow.RestoreSizeToContent(new Window { Width = DesignWidth, SizeToContent = SizeToContent.Height }, DesignWidth))
            .ShouldBeFalse();
}
