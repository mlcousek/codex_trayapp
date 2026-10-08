using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexTrayApp.Core.Diagnostics;

/// <summary>
/// Renders the shape of a JSON document: keys, value kinds, numbers and booleans. String values are hidden
/// unless they are short and asked for, so an undocumented response can be logged once without leaking anything.
/// </summary>
public static class JsonShapeDescriber
{
    /// <summary>Strings up to this length may be shown when <c>includeShortStrings</c> is set; longer ones never are.</summary>
    public const int ShortStringLimit = 24;

    /// <summary>Arrays show this many elements in full; longer ones are truncated with an ellipsis.</summary>
    public const int MaxArrayItems = 5;

    public static string Describe(JsonElement element, int maxDepth = 4, bool includeShortStrings = false)
    {
        var builder = new StringBuilder();
        Write(builder, element, 0, maxDepth, includeShortStrings);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonElement element, int depth, int maxDepth, bool includeShortStrings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= maxDepth)
                {
                    builder.Append("{...}");
                    return;
                }

                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(property.Name).Append(": ");
                    Write(builder, property.Value, depth + 1, maxDepth, includeShortStrings);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append(CultureInfo.InvariantCulture, $"[{element.GetArrayLength()}:");
                var shown = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (shown == MaxArrayItems)
                    {
                        builder.Append(", ...");
                        break;
                    }

                    builder.Append(shown == 0 ? " " : ", ");
                    Write(builder, item, depth + 1, maxDepth, includeShortStrings);
                    shown++;
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (includeShortStrings && text.Length <= ShortStringLimit && !text.Contains('@', StringComparison.Ordinal))
                {
                    builder.Append('"').Append(text).Append('"');
                }
                else
                {
                    builder.Append(CultureInfo.InvariantCulture, $"string({text.Length})");
                }

                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                builder.Append(element.GetRawText());
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                builder.Append("undefined");
                break;
        }
    }
}
