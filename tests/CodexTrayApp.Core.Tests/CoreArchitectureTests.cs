using CodexTrayApp.Core;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

/// <summary>Guards the rule that Core never references WPF or any other UI assembly.</summary>
public class CoreArchitectureTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "System.Windows",
        "H.NotifyIcon",
    ];

    [Fact]
    public void Core_does_not_reference_any_ui_assembly()
    {
        var referenced = typeof(AppPaths).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced
            .Where(name => ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .ShouldBeEmpty();
    }
}
