using CodexTrayApp.Core.Diagnostics;
using Shouldly;

namespace CodexTrayApp.Core.Tests;

public class TokenFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(845, "845")]
    [InlineData(1_234, "1.2k")]
    [InlineData(12_345, "12k")]
    [InlineData(999_999, "1000k")]
    [InlineData(4_200_000, "4.2M")]
    [InlineData(12_345_678, "12M")]
    [InlineData(1_100_000_000, "1.1B")]
    public void Compacts_token_counts(long value, string expected) =>
        TokenFormat.Compact(value).ShouldBe(expected);

    [Theory]
    [InlineData(0.0, "USD", "$0.00")]
    [InlineData(4.2, "USD", "$4.20")]
    [InlineData(123.456, "USD", "$123.5")]
    [InlineData(3.5, "EUR", "3.50 EUR")]
    public void Formats_money(decimal amount, string currency, string expected) =>
        TokenFormat.Money(amount, currency).ShouldBe(expected);
}
