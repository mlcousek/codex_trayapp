namespace CodexTrayApp.Core.Domain;
public static class WindowNameHumanizer
{
    public static string Humanize(string key) => key switch
    {
        "codex:primary" => "Current window",
        "codex:secondary" => "Secondary window",
        _ => Capitalize(key.Replace(":", " \u00b7 ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal)),
    };
    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
