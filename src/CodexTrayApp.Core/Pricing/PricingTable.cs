using System.Globalization;
using System.Text.Json;
using CodexTrayApp.Core.Analytics;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Pricing;

public enum PriceMatch
{
    Exact,
    Prefix,
}

/// <summary>Prices per million tokens for one model id, or for every id starting with it.</summary>
public sealed record ModelPrice(
    string Id,
    PriceMatch Match,
    decimal Input,
    decimal Output,
    decimal CacheWrite5m,
    decimal CacheWrite1h,
    decimal CacheRead);

/// <summary>
/// Data-driven pricing: loaded from <c>pricing.json</c>, never hardcoded. Unknown models get no price at all,
/// so a cost is either right or absent, never a guess.
/// </summary>
public sealed class PricingTable
{
    public PricingTable(DateOnly? effectiveDate, string currency, IReadOnlyList<ModelPrice> models, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(models);
        EffectiveDate = effectiveDate;
        Currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency;
        Models = models;
        SourcePath = sourcePath;
    }

    public static PricingTable Empty { get; } = new(null, "USD", [], null);

    public DateOnly? EffectiveDate { get; }

    public string Currency { get; }

    public IReadOnlyList<ModelPrice> Models { get; }

    public string? SourcePath { get; }

    public bool IsEmpty => Models.Count == 0;

    /// <summary>Exact id first, then the longest prefix entry that matches.</summary>
    public ModelPrice? Find(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        ModelPrice? best = null;
        foreach (var price in Models)
        {
            if (string.Equals(price.Id, model, StringComparison.OrdinalIgnoreCase))
            {
                return price;
            }

            if (price.Match == PriceMatch.Prefix
                && model.StartsWith(price.Id, StringComparison.OrdinalIgnoreCase)
                && (best is null || price.Id.Length > best.Id.Length))
            {
                best = price;
            }
        }

        return best;
    }

    public decimal? Cost(string model, TokenTotals tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (Find(model) is not { } price)
        {
            return null;
        }

        var cost = (tokens.Input * price.Input)
            + (tokens.Output * price.Output)
            + (tokens.CacheWrite5m * price.CacheWrite5m)
            + (tokens.CacheWrite1h * price.CacheWrite1h)
            + (tokens.CacheRead * price.CacheRead);
        return cost / 1_000_000m;
    }
}

/// <summary>Reads <c>pricing.json</c>: an override path first, then the file shipped next to the binary.</summary>
public static class PricingLoader
{
    public static PricingTable Load(string? overridePath, string bundledPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var candidate in new[] { overridePath, bundledPath })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                if (!File.Exists(candidate))
                {
                    logger.LogWarning("Pricing file {Path} does not exist", candidate);
                    continue;
                }

                var table = Parse(File.ReadAllText(candidate), candidate);
                logger.LogInformation("Pricing loaded from {Path}: {Count} models, effective {Date}", candidate, table.Models.Count, table.EffectiveDate);
                return table;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                logger.LogWarning("Pricing file {Path} could not be used ({Reason}); costs will show as unknown", candidate, ex.GetType().Name);
            }
        }

        return PricingTable.Empty;
    }

    internal static PricingTable Parse(string json, string? sourcePath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("pricing.json needs a 'models' array.");
        }

        DateOnly? effective = root.TryGetProperty("effectiveDate", out var date) && date.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            ? parsedDate
            : null;
        var currency = root.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString()! : "USD";

        var list = new List<ModelPrice>();
        foreach (var entry in models.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // A missing or malformed input/output price is treated the same as an unrecognized model id: the
            // entry is skipped so lookups later degrade to "cost unknown" instead of silently pricing at $0.
            if (!TryDecimal(entry, "input", out var input) || !TryDecimal(entry, "output", out var output))
            {
                continue;
            }

            var match = entry.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.String
                && string.Equals(m.GetString(), "prefix", StringComparison.OrdinalIgnoreCase)
                ? PriceMatch.Prefix
                : PriceMatch.Exact;
            list.Add(new ModelPrice(
                id.GetString()!,
                match,
                input,
                output,
                entry.TryGetProperty("cacheWrite5m", out _) ? Decimal(entry, "cacheWrite5m") : input * 1.25m,
                entry.TryGetProperty("cacheWrite1h", out _) ? Decimal(entry, "cacheWrite1h") : input * 2m,
                entry.TryGetProperty("cacheRead", out _) ? Decimal(entry, "cacheRead") : input * 0.1m));
        }

        return new PricingTable(effective, currency, list, sourcePath);
    }

    private static decimal Decimal(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : 0m;

    /// <summary>True, with the parsed value, only when <paramref name="name"/> is present and a JSON number.</summary>
    private static bool TryDecimal(JsonElement entry, string name, out decimal value)
    {
        if (entry.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            value = element.GetDecimal();
            return true;
        }

        value = 0m;
        return false;
    }
}
