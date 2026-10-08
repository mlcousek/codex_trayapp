using System.Globalization;

namespace CodexTrayApp.Core.Diagnostics;

/// <summary>Compact numbers for the UI: 845, 12.3k, 4.2M, 1.1B. Money keeps two decimals under ten.</summary>
public static class TokenFormat
{
    public static string Compact(double value)
    {
        var magnitude = Math.Abs(value);
        return magnitude switch
        {
            >= 1_000_000_000 => Scaled(value / 1_000_000_000, "B"),
            >= 1_000_000 => Scaled(value / 1_000_000, "M"),
            >= 1_000 => Scaled(value / 1_000, "k"),
            _ => Math.Round(value).ToString("0", CultureInfo.InvariantCulture),
        };
    }

    public static string Compact(long value) => Compact((double)value);

    public static string Money(decimal amount, string currency)
    {
        var number = amount.ToString(amount < 10 ? "0.00" : "0.#", CultureInfo.InvariantCulture);
        return string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" + number : number + " " + currency;
    }

    private static string Scaled(double value, string suffix)
    {
        var text = Math.Abs(value) < 10
            ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        return text + suffix;
    }
}
