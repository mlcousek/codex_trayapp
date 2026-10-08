using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Updates;

/// <summary>A published release of this app, as the GitHub releases API reports it.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Url);

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
    Unavailable,
}

/// <summary>The result of one check. A failure is never surfaced to the user; it only lands in the log.</summary>
public sealed record UpdateCheckOutcome(UpdateCheckStatus Status, ReleaseInfo? Release, string? Message)
{
    public static UpdateCheckOutcome UpToDate() => new(UpdateCheckStatus.UpToDate, null, null);

    public static UpdateCheckOutcome Available(ReleaseInfo release) => new(UpdateCheckStatus.UpdateAvailable, release, null);

    public static UpdateCheckOutcome Failed(string message) => new(UpdateCheckStatus.Failed, null, message);
    public static UpdateCheckOutcome Unavailable() => new(UpdateCheckStatus.Unavailable, null, "No release repository is configured.");
}

/// <summary>
/// Asks the GitHub releases API whether a newer version of this app has been published. It sends nothing about the
/// user: an unauthenticated GET, no query string, no body. Only reached when the user leaves the update check on.
/// Every failure is swallowed into <see cref="UpdateCheckStatus.Failed"/>; a check that cannot run is never an error
/// the user has to see.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly Version _current;
    private readonly string? _repository;
    private readonly ILogger<UpdateChecker> _logger;

    public UpdateChecker(HttpClient http, string currentVersion, ILogger<UpdateChecker> logger, string? repository = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _current = Normalize(ParseVersion(currentVersion) ?? new Version(0, 0, 0));
        _repository = NormalizeRepository(repository);
        _logger = logger;
    }

    /// <summary>Where to send someone who wants the new version. Opened by the settings window, never automatically.</summary>
    public string ReleasesPageUrl => IsConfigured ? $"https://github.com/{_repository}/releases/latest" : string.Empty;
    public bool IsConfigured => _repository is not null;
    public string? Repository => _repository;

    /// <summary>The version this build reports, after normalisation.</summary>
    public Version CurrentVersion => _current;

    /// <summary>One check. Returns an outcome rather than throwing, whatever the network or the API does.</summary>
    public async Task<UpdateCheckOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured) { return UpdateCheckOutcome.Unavailable(); }
        var url = $"https://api.github.com/repos/{_repository}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"CodexUsageTray/{_current}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogDebug("Update check could not reach GitHub: {Reason}", ex.GetType().Name);
            return UpdateCheckOutcome.Failed("GitHub could not be reached.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _logger.LogDebug("Update check was rate limited by GitHub");
                return UpdateCheckOutcome.Failed("GitHub rate-limited the update check.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check returned HTTP {Status}", (int)response.StatusCode);
                return UpdateCheckOutcome.Failed($"GitHub answered HTTP {(int)response.StatusCode}.");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                _logger.LogDebug("Update check response could not be read: {Reason}", ex.GetType().Name);
                return UpdateCheckOutcome.Failed("The GitHub response was cut off.");
            }

            return Interpret(body);
        }
    }

    /// <summary>Turns a releases-API body into an outcome. Split out so the comparison is testable without a network.</summary>
    internal UpdateCheckOutcome Interpret(string body)
    {
        string? tag;
        string? url;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return UpdateCheckOutcome.Failed("The GitHub response was not a release object."); }
            tag = root.TryGetProperty("tag_name", out var tagElement) && tagElement.ValueKind == JsonValueKind.String
                ? tagElement.GetString()
                : null;
            url = root.TryGetProperty("html_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return UpdateCheckOutcome.Failed("The GitHub response was not valid JSON.");
        }

        if (ParseVersion(tag) is not { } published)
        {
            // A tag we cannot read (or one carrying a prerelease suffix) is not something to nag anyone about.
            _logger.LogDebug("Update check ignored a release tag it could not read");
            return UpdateCheckOutcome.Failed("The latest release tag could not be read.");
        }

        if (Normalize(published) <= _current)
        {
            return UpdateCheckOutcome.UpToDate();
        }

        var release = new ReleaseInfo(Normalize(published), tag!, IsReleaseUrl(url) ? url! : ReleasesPageUrl);
        _logger.LogInformation("Update available: {Version} (this build is {Current})", release.Version, _current);
        return UpdateCheckOutcome.Available(release);
    }

    private static string? NormalizeRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) { return null; }
        var value = repository.Trim();
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p is not ("." or "..")
            && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ? value : null;
    }

    private bool IsReleaseUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/" + _repository + "/releases/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"v0.1.1" and "0.1.1" both parse; a prerelease such as "v0.2.0-beta.1" deliberately does not.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Prereleases and build metadata are skipped: only a finished release is worth telling someone about.
        return text.Contains('-', StringComparison.Ordinal) || text.Contains('+', StringComparison.Ordinal)
            ? null
            : Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// Compares on three components. Version treats an unspecified revision as -1, so 0.1.1 would otherwise sort
    /// below 0.1.1.0 and every check would report an update.
    /// </summary>
    internal static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
