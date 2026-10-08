using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodexTrayApp.Controls;

/// <summary>Visible for a non-empty string, collapsed otherwise. Lets a note line vanish when there is nothing to say.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
