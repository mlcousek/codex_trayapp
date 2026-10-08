using System.Globalization;
using System.Windows;
using CodexTrayApp.Controls;
using Shouldly;

namespace CodexTrayApp.Tests;

/// <summary>Every note line in the settings window and flyout vanishes through this converter when it has nothing to say.</summary>
public class TextToVisibilityConverterTests
{
    private readonly TextToVisibilityConverter _converter = new();

    [Fact]
    public void Text_is_visible() =>
        Convert("Windows refused the change").ShouldBe(Visibility.Visible);

    [Fact]
    public void Whitespace_still_counts_as_something_to_show() =>
        Convert(" ").ShouldBe(Visibility.Visible);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_missing_text_collapses(string? value) =>
        Convert(value).ShouldBe(Visibility.Collapsed);

    [Fact]
    public void Anything_that_is_not_text_collapses() =>
        Convert(42).ShouldBe(Visibility.Collapsed);

    [Fact]
    public void Converting_back_is_not_supported() =>
        Should.Throw<NotSupportedException>(() => _converter.ConvertBack(Visibility.Visible, typeof(string), null, CultureInfo.InvariantCulture));

    private object Convert(object? value) => _converter.Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);
}
