using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexTrayApp.Interop;
using CodexTrayApp.Theming;
using CodexTrayApp.Tray;
using CodexTrayApp.ViewModels;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Views;

/// <summary>
/// The flyout: a tool window anchored to the taskbar edge next to the tray icon, with an acrylic backdrop when
/// Windows provides one and a solid surface otherwise. Hidden, never closed, so it reopens in a few milliseconds.
/// </summary>
public partial class FlyoutWindow : Window
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(250);
    private readonly FlyoutViewModel _viewModel;
    private readonly ThemeManager _theme;
    private readonly ILogger<FlyoutWindow> _logger;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly double _designWidth;
    private nint _handle;
    private DateTime _hiddenAt = DateTime.MinValue;
    private POINT _anchor;
    private bool _placing;

    public FlyoutWindow(FlyoutViewModel viewModel, ThemeManager theme, ILogger<FlyoutWindow> logger)
    {
        _viewModel = viewModel;
        _theme = theme;
        _logger = logger;

        InitializeComponent();
        _designWidth = Width;
        DataContext = viewModel;

        _tick.Tick += (_, _) => _viewModel.Tick();
        Deactivated += (_, _) => HideFlyout();
        SizeChanged += OnSizeChanged;
        _theme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Creates the native window and runs one layout pass off-screen, so the first real open pays only for
    /// placement and paint instead of building the visual tree.
    /// </summary>
    public void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Left = -32000;
        Top = -32000;
        ShowActivated = false;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
        _hiddenAt = DateTime.MinValue;
        _viewModel.PreloadCharts();
    }

    /// <summary>Opens or closes the flyout. A click on the tray icon that just dismissed it does not reopen it.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        if (DateTime.UtcNow - _hiddenAt < ReopenGuard)
        {
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        var stopwatch = Stopwatch.StartNew();
        _viewModel.OnShown();

        NativeMethods.GetCursorPos(out _anchor);
        Show();
        UpdateLayout();
        Place(_anchor);
        Activate();
        RefreshButton.Focus();
        FadeIn();
        _tick.Start();

        _logger.LogDebug("Flyout shown in {Elapsed} ms", stopwatch.ElapsedMilliseconds);
    }

    public void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }

        _tick.Stop();
        _viewModel.OnHidden();
        Hide();
        _hiddenAt = DateTime.UtcNow;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        MakeToolWindow();
        ApplyBackdrop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Windows answers a DPI change (the flyout moved to another monitor, or monitors were reconnected, even while it
    /// was hidden) with a rescaled rectangle that WPF applies as a manual resize. Once the layout has settled, put the
    /// size contract back and, if the flyout is open, re-anchor it to the icon it was opened from.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _logger.LogDebug("Flyout DPI changed from {Old} to {New}", oldDpi.PixelsPerDip, newDpi.PixelsPerDip);
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_placing)
                {
                    return;
                }

                RestoreSizeToContent(this, _designWidth);
                if (IsVisible)
                {
                    Place(_anchor);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Content that arrives after the open (chart data, wrapped text) changes the height; keep hugging the taskbar edge.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible && e.HeightChanged)
        {
            Place(_anchor);
        }
    }

    /// <summary>
    /// The flyout's layout contract: the design width, and a height that follows the content. A DPI change breaks
    /// both (SizeToContent drops to Manual, the width is rescaled and the height sticks at the work area), so this runs
    /// before every measurement. Returns true when anything had to be put back.
    /// </summary>
    internal static bool RestoreSizeToContent(Window window, double designWidth)
    {
        ArgumentNullException.ThrowIfNull(window);
        var broken = window.SizeToContent != SizeToContent.Height || Math.Abs(window.Width - designWidth) > 0.5;
        if (!broken)
        {
            return false;
        }

        window.Width = designWidth;
        window.ClearValue(HeightProperty);
        window.SizeToContent = SizeToContent.Height;
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Marks the flyout as not visible, same as HideFlyout, so RefreshCharts guards itself if a chart load
        // completes after the window closes during shutdown.
        _viewModel.OnHidden();
        _theme.ThemeChanged -= OnThemeChanged;
        _tick.Stop();
        base.OnClosed(e);
    }

    /// <summary>
    /// Positions the window in physical pixels on the monitor under the anchor, hugging the taskbar edge. Re-entrant
    /// calls (the resize and DPI events this placement itself causes) are ignored rather than allowed to bounce.
    /// </summary>
    private void Place(POINT anchor)
    {
        if (_handle == 0 || _placing)
        {
            return;
        }

        _placing = true;
        try
        {
            PlaceOnMonitor(anchor);
        }
        finally
        {
            _placing = false;
        }
    }

    private void PlaceOnMonitor(POINT anchor)
    {
        var monitor = NativeMethods.MonitorFromPoint(anchor, NativeMethods.MonitorDefaultToNearest);
        var info = new MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        var margin = (int)Math.Round(12 * scale);

        // Step onto the target monitor first when its DPI differs from the window's. The DPI change (and WPF's
        // rescaled resize) then happens here, before anything is measured, instead of during the final move, where
        // it used to leave a half-width, work-area-tall window behind.
        if (Math.Abs(VisualTreeHelper.GetDpi(this).DpiScaleX - scale) > 0.01)
        {
            NativeMethods.SetWindowPos(_handle, 0, info.Work.Left + margin, info.Work.Top + margin, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            _logger.LogDebug("Flyout moved onto a monitor at {Scale:0.##}x before measuring", scale);
        }

        if (RestoreSizeToContent(this, _designWidth))
        {
            _logger.LogDebug("Flyout size restored to {Width} DIP wide, height from content", _designWidth);
        }

        // Never taller than the work area: the body scrolls instead.
        var maxHeight = Math.Floor((info.Work.Bottom - info.Work.Top - (2 * margin)) / scale);
        if (maxHeight > 0 && Math.Abs(MaxHeight - maxHeight) > 0.5)
        {
            MaxHeight = maxHeight;
        }

        UpdateLayout();

        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var (x, y, edge) = FlyoutPlacement.Compute(
            new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
            anchor.X,
            anchor.Y,
            width,
            height,
            margin);

        NativeMethods.SetWindowPos(_handle, 0, x, y, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        _logger.LogDebug("Flyout placed at ({X},{Y}) on the {Edge} edge, {Width}x{Height} px", x, y, edge, width, height);
    }

    /// <summary>WS_EX_TOOLWINDOW keeps the flyout out of Alt-Tab and off the taskbar.</summary>
    private void MakeToolWindow()
    {
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, style | NativeMethods.WsExToolWindow);
    }

    private void ApplyBackdrop()
    {
        ApplyDarkMode();

        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var margins = SystemParameters.HighContrast ? new MARGINS() : new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var backdrop = SystemParameters.HighContrast ? 1 : NativeMethods.DwmsbtTransientWindow;
        var extended = NativeMethods.DwmExtendFrameIntoClientArea(_handle, ref margins) == 0;
        var applied = NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        var supported = !SystemParameters.HighContrast && extended && applied;

        if (supported)
        {
            Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(_handle) is { } source)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            RootBorder.SetResourceReference(BackgroundProperty, "SurfaceTranslucentBrush");
        }
        else
        {
            RootBorder.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        }

        _logger.LogDebug("Flyout backdrop: {Mode}", supported ? "acrylic" : "solid");
    }

    private void ApplyDarkMode()
    {
        if (_handle == 0)
        {
            return;
        }

        var dark = _theme.AppsTheme == AppTheme.Dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    /// <summary>A 120 ms fade on the content, skipped when Windows animations are turned off.</summary>
    private void FadeIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Body.BeginAnimation(OpacityProperty, null);
            Body.Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        Body.BeginAnimation(OpacityProperty, animation);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBackdrop();
}
