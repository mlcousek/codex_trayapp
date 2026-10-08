using System.Text.RegularExpressions;

namespace CodexTrayApp.Core.Security;

/// <summary>Scrubs token-shaped content from text before it can reach a log, a crash report or the UI.</summary>
public static partial class SecretRedactor
{
    public const string Mask = "[redacted]";

    /// <summary>Removes bearer tokens, OpenAI keys and JSON token fields from arbitrary text.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = BearerPattern().Replace(text, "$1" + Mask);
        result = OpenAIKeyPattern().Replace(result, Mask);
        result = JsonTokenPattern().Replace(result, "\"$1\": \"" + Mask + "\"");
        return result;
    }

    /// <summary>Like <see cref="Redact(string?)"/>, and additionally removes one known secret verbatim.</summary>
    public static string Redact(string? text, string? secret)
    {
        var result = Redact(text);
        return string.IsNullOrEmpty(secret) || secret.Length < 8
            ? result
            : result.Replace(secret, Mask, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(bearer\s+)[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9\-_]{8,}")]
    private static partial Regex OpenAIKeyPattern();

    [GeneratedRegex(@"""(accessToken|refreshToken|access_token|refresh_token|token|apiKey|api_key)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonTokenPattern();
}
