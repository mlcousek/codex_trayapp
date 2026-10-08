using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CodexTrayApp.Interop;
using CodexTrayApp.Theming;
using CodexTrayApp.ViewModels;

namespace CodexTrayApp.Views;

/// <summary>The settings window: an ordinary top-level window with a dark title bar when the app is dark.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly ThemeManager _theme;
    private nint _handle;

    public SettingsWindow(SettingsViewModel viewModel, ThemeManager theme)
    {
        _viewModel = viewModel;
        _theme = theme;

        InitializeComponent();
        DataContext = viewModel;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 48);

        CommitOnLeave(PollIntervalBox, viewModel.ApplyPollIntervalCommand);
        CommitOnLeave(RetentionBox, viewModel.ApplyRetentionCommand);
        CommitOnLeave(ThresholdsBox, viewModel.ApplyThresholdsCommand);
        CommitOnLeave(PricingPathBox, viewModel.ApplyPricingPathCommand);
        ClearHistoryButton.Click += OnClearHistory;
        CloseButton.Click += (_, _) => Close();
        _theme.ThemeChanged += OnThemeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        ApplyDarkMode();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.RefreshAutostart();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    /// <summary>Text fields apply when the user leaves them or presses Enter; typing alone changes nothing.</summary>
    private static void CommitOnLeave(TextBox box, ICommand command)
    {
        box.LostKeyboardFocus += (_, _) => command.Execute(null);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                command.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Remove every recorded percentage and token total from the local database? Codex session logs are preserved. Only new activity will be recorded after clearing.",
            "Clear history",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            _viewModel.ClearHistoryCommand.Execute(null);
        }
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

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyDarkMode();
}
