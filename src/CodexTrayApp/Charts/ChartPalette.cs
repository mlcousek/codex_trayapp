using System.Windows;
using System.Windows.Media;

namespace CodexTrayApp.Charts;

/// <summary>
/// Resolves chart brushes from the theme resources, so charts follow the active palette like every other view. The
/// resolved brushes are the theme's own instances, whose colours change with the palette.
/// </summary>
public sealed class ChartPalette
{
    /// <summary>Number of distinct series colours in the palette; further series wrap around.</summary>
    public const int SeriesCount = 5;

    private readonly Func<string, Brush?> _resolve;

    public ChartPalette(Func<string, Brush?> resolve)
    {
        _resolve = resolve;
    }

    public Brush Danger => Resolve("DangerBrush");

    public Brush Secondary => Resolve("TextSecondaryBrush");

    public static ChartPalette FromApplication() => new(key => Application.Current?.TryFindResource(key) as Brush);

    /// <summary>Series colour by rank: the accent first, then blue, green, amber and the secondary text tone.</summary>
    public Brush Series(int index) => Resolve("Chart" + ((Math.Max(0, index) % SeriesCount) + 1) + "Brush");

    private Brush Resolve(string key) => _resolve(key) ?? Brushes.Gray;
}
