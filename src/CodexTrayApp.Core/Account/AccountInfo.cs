namespace CodexTrayApp.Core.Account;

/// <summary>Who is signed in, as Codex CLI recorded it. Read-only, and never logged in full.</summary>
public sealed record AccountInfo(
    string? Email,
    string? DisplayName,
    string? OrganizationName,
    string? BillingType,
    string? OrganizationRateLimitTier,
    bool? HasExtraUsageEnabled)
{
    public static AccountInfo Empty { get; } = new(null, null, null, null, null, null);

    public bool IsEmpty => Email is null && DisplayName is null && OrganizationName is null;

    /// <summary>"s***@example.com": first character, three stars, the domain. Reveals neither length nor spelling.</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }

    public override string ToString() => $"AccountInfo({MaskEmail(Email)}, {OrganizationName})";
}

public interface IAccountInfoSource
{
    Task<AccountInfo> ReadAsync(CancellationToken cancellationToken);
}
