using CodexTrayApp.Core.Diagnostics;
using CodexTrayApp.Core.Polling;
using CodexTrayApp.Core.Providers;

namespace CodexTrayApp.ViewModels;

internal static class ConnectionStatusText
{
    public static string Label(PollStatus status) => status.State == PollState.Fetching ? "Refreshing" : status.Failure switch
    {
        UsageFetchStatus.CliMissing => "Codex CLI not found",
        UsageFetchStatus.ApiKeyUnsupported => "API-key sign-in",
        UsageFetchStatus.CliIncompatible => "Codex CLI update required",
        UsageFetchStatus.NetworkError => "Connection unavailable",
        UsageFetchStatus.ParseError or UsageFetchStatus.SchemaChanged => "Usage data unavailable",
        _ => status.State switch
        {
            PollState.Unauthenticated => "Not signed in",
            PollState.RateLimited => "Rate limited",
            PollState.Stale => "Usage unavailable",
            PollState.Idle => "Waiting for refresh",
            PollState.Fetching => "Refreshing",
            _ => "Connected",
        },
    };

    public static string? Banner(PollStatus status, DateTimeOffset now)
    {
        if (status.State == PollState.Ok) { return null; }
        var guidance = status.State switch
        {
            PollState.Fetching => "Checking account usage…",
            PollState.Idle => "Waiting for the first refresh.",
            _ => status.Message ?? (status.State == PollState.Unauthenticated
                ? "Run codex login to sign in with ChatGPT, then refresh."
                : "Check your connection. The app will retry automatically."),
        };
        var cached = status.Snapshot is not null ? " Showing the last known values." : string.Empty;
        var retry = status.State is not (PollState.Fetching or PollState.Idle) && status.NextAttempt is { } next
            ? next > now ? " Next retry in " + UsageSnapshotFormatter.FormatDuration(next - now) + "."
                : " Retry is due now."
            : string.Empty;
        return Label(status) + ". " + guidance + cached + retry;
    }
}
