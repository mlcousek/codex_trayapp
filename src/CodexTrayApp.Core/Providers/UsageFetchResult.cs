using CodexTrayApp.Core.Domain;

namespace CodexTrayApp.Core.Providers;

public enum UsageFetchStatus
{
    Success,
    NoCredentials,
    TokenExpired,
    Unauthenticated,
    RateLimited,
    ServerError,
    NetworkError,
    ParseError,

    /// <summary>
    /// The endpoint answered 200 with readable JSON that carried no usage windows at all. Either its undocumented
    /// shape changed or this account reports none; both mean percentages cannot be shown, and neither is invented.
    /// </summary>
    SchemaChanged,
    CliMissing,
    ApiKeyUnsupported,
    CliIncompatible,
}

/// <summary>Outcome of one fetch. <see cref="Message"/> is user-facing and never contains a secret.</summary>
public sealed record UsageFetchResult(UsageFetchStatus Status, UsageSnapshot? Snapshot, TimeSpan? RetryAfter, string? Message)
{
    public bool IsSuccess => Status == UsageFetchStatus.Success;

    public static UsageFetchResult Success(UsageSnapshot snapshot) => new(UsageFetchStatus.Success, snapshot, null, null);

    public static UsageFetchResult Failed(UsageFetchStatus status, string message, TimeSpan? retryAfter = null) =>
        new(status, null, retryAfter, message);
}

/// <summary>Anything that can produce a usage snapshot. Knows about transport, never about UI.</summary>
public interface IUsageProvider
{
    string Name { get; }

    Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Signals when a verified account or source changes, before new quota is requested.</summary>
public interface IAccountChangeSource
{
    event EventHandler<AccountIdentityChangedEventArgs>? AccountChanged;
}

public sealed class AccountIdentityChangedEventArgs(string? identityKey, string? accountKey = null, string? sourceKey = null) : EventArgs
{
    public string? IdentityKey { get; } = identityKey;
    public string? AccountKey { get; } = accountKey;
    public string? SourceKey { get; } = sourceKey;
}
