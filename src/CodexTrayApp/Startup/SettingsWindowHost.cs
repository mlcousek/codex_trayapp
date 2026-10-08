using System.Windows;
using CodexTrayApp.Theming;
using CodexTrayApp.ViewModels;
using CodexTrayApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CodexTrayApp.Startup;

/// <summary>Opens the settings window at most once at a time; a second request brings the open one forward.</summary>
public sealed class SettingsWindowHost
{
    private readonly IServiceProvider _services;

    public SettingsWindowHost(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>The open window, or null.</summary>
    public SettingsWindow? Current { get; private set; }

    public void Show()
    {
        if (Current is null)
        {
            var window = new SettingsWindow(_services.GetRequiredService<SettingsViewModel>(), _services.GetRequiredService<ThemeManager>());
            window.Closed += (_, _) => Current = null;
            Current = window;
            window.Show();
            return;
        }

        if (Current.WindowState == WindowState.Minimized)
        {
            Current.WindowState = WindowState.Normal;
        }

        Current.Activate();
    }

    public void Close() => Current?.Close();
}
