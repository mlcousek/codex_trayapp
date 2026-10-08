using CodexTrayApp.Interop;
using CodexTrayApp.Tray;
using Shouldly;

namespace CodexTrayApp.Tests;

public class TrayIconControllerTests
{
    /// <summary>
    /// GetDpiForSystem is a real, read-only Win32 API call - safe to call for real, no mocking needed. Rather than
    /// asserting an arbitrary bound, recompute the same clamp the source applies and check IconPixelSize agrees with
    /// it for whatever DPI this machine (or CI runner) actually reports.
    /// </summary>
    [Fact]
    public void Icon_pixel_size_matches_the_documented_clamp_for_the_real_system_dpi()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != 0 ? NativeMethods.GetDpiForWindow(taskbar) : 0;
        if (dpi == 0) { dpi = NativeMethods.GetDpiForSystem(); }
        var expected = Math.Clamp((int)Math.Round(16 * dpi / 96.0), 16, 64);

        var size = TrayIconController.IconPixelSize();

        size.ShouldBe(expected);
        size.ShouldBeInRange(16, 64);
    }
}
