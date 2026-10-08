using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTrayApp.Core.Account;
using CodexTrayApp.Core.Domain;
using CodexTrayApp.Core.Providers;
using CodexTrayApp.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Codex;

public interface ICodexRpcClientFactory
{
    Task<ICodexRpcClient> StartAsync(string executable, string home, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class CodexRpcClientFactory : ICodexRpcClientFactory
{
    public async Task<ICodexRpcClient> StartAsync(string executable, string home, string workingDirectory, CancellationToken cancellationToken) =>
        await CodexRpcClient.StartAsync(executable, home, workingDirectory, cancellationToken).ConfigureAwait(false);
}

public sealed class CodexUsageProvider(AppPaths paths, SettingsStore settings, ILogger<CodexUsageProvider> logger, ICodexRpcClientFactory? rpcFactory = null) : IUsageProvider, IAccountInfoSource, IAccountChangeSource, IDisposable
{
    private readonly SemaphoreSlim _fetch = new(1);
    private readonly ICodexRpcClientFactory _rpcFactory = rpcFactory ?? new CodexRpcClientFactory();
    private AccountInfo _account = AccountInfo.Empty;
    private string? _identity;
    private string? _lastAccountKey;
    public event EventHandler<AccountIdentityChangedEventArgs>? AccountChanged;
    public string Name => "Codex app-server";
    public void Dispose() => _fetch.Dispose();
    public Task<AccountInfo> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(_account);
    public async Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        await _fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = settings.Current;
            var executable = LocateExecutable(options.CodexExecutablePath);
            if (executable is null) { SetIdentity("unavailable"); return UsageFetchResult.Failed(UsageFetchStatus.CliMissing, "Install Codex CLI or select its executable in Settings."); }
            var home = Path.GetFullPath(options.CodexHomePath ?? paths.CodexHome);
            AccountChanged?.Invoke(this, new AccountIdentityChangedEventArgs(null, sourceKey: IdentityKey(home)));
            // A home override invalidates the previous account even if the new connection fails.
            if (_identity is not null && !_identity.StartsWith(home + "|", StringComparison.Ordinal)) { SetIdentity(home + "|unverified"); }
            var client = await _rpcFactory.StartAsync(executable, home, paths.LocalRoot, cancellationToken).ConfigureAwait(false);
            await using var connection = client.ConfigureAwait(false);
            var account = await client.RequestAsync("account/read", new { refreshToken = true }, cancellationToken).ConfigureAwait(false);
            if (account.ValueKind != JsonValueKind.Object) { throw new JsonException("Codex account must be an object."); }
            if (!account.TryGetProperty("account", out var identity) || identity.ValueKind != JsonValueKind.Object)
            {
                SetIdentity(home + "|signed-out");
                return UsageFetchResult.Failed(UsageFetchStatus.NoCredentials, "Run codex login to sign in with ChatGPT.");
            }
            if (Text(identity, "type") != "chatgpt")
            {
                SetIdentity(home + "|" + (Text(identity, "type") ?? "unknown"));
                return UsageFetchResult.Failed(UsageFetchStatus.ApiKeyUnsupported, "Subscription limits require ChatGPT sign-in. Local analytics still work with API keys.");
            }
            var email = Text(identity, "email");
            SetIdentity(home + "|chatgpt|" + (email ?? "unidentified"));
            _account = new AccountInfo(email, null, null, null, Text(identity, "planType"), null);
            var limits = await client.RequestAsync("account/rateLimits/read", null, cancellationToken).ConfigureAwait(false);
            var snapshot = CodexUsageParser.Parse(limits, identity, DateTimeOffset.UtcNow) with
            {
                AccountIdentityKey = IdentityKey(_identity!),
                AccountSourceKey = IdentityKey(home),
            };
            if (_lastAccountKey != snapshot.AccountKey)
            {
                _lastAccountKey = snapshot.AccountKey;
                AccountChanged?.Invoke(this, new AccountIdentityChangedEventArgs(snapshot.AccountIdentityKey, snapshot.AccountKey));
            }
            return snapshot.Windows.Count == 0 ? UsageFetchResult.Failed(UsageFetchStatus.SchemaChanged, "Codex did not return quota windows for this account.") : UsageFetchResult.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CodexRpcException ex)
        {
            logger.LogWarning("Codex account request failed with RPC code {Code}", ex.Code);
            if (ex.Code == -32601)
            {
                return UsageFetchResult.Failed(UsageFetchStatus.CliIncompatible, "This Codex CLI version does not support account usage. Update the Codex CLI and try again.");
            }

            return UsageFetchResult.Failed(UsageFetchStatus.ServerError, "Codex could not read account usage. Check your sign-in and CLI version.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Codex connection unavailable ({Reason})", ex.GetType().Name);
            return UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "Codex usage is unavailable. Check your connection and CLI sign-in.");
        }
        finally { _fetch.Release(); }
    }
    private void SetIdentity(string identity)
    {
        if (string.Equals(_identity, identity, StringComparison.Ordinal)) { return; }
        _identity = identity;
        _lastAccountKey = null;
        _account = AccountInfo.Empty;
        AccountChanged?.Invoke(this, new AccountIdentityChangedEventArgs(IdentityKey(identity)));
    }
    private static string IdentityKey(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    internal static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static string? LocateExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) { return File.Exists(configured) ? configured : null; }
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin"));
        return directories.Select(p => Path.Combine(p, "codex.exe")).FirstOrDefault(File.Exists);
    }
}

public static class CodexUsageParser
{
    public static UsageSnapshot Parse(JsonElement limits, JsonElement account, DateTimeOffset now)
    {
        if (limits.ValueKind != JsonValueKind.Object) { throw new JsonException("Codex limits must be an object."); }
        var windows = new List<UsageWindow>();
        string? credits = null;
        string? plan = CodexUsageProvider.Text(account, "planType");
        var buckets = limits.TryGetProperty("rateLimitsByLimitId", out var multi) && multi.ValueKind == JsonValueKind.Object && multi.EnumerateObject().Any()
            ? multi.EnumerateObject().Select(p => (p.Name, p.Value)).OrderBy(p => p.Name == "codex" ? 0 : 1).ThenBy(p => p.Name, StringComparer.Ordinal).ToList()
            : limits.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object ? [(CodexUsageProvider.Text(single, "limitId") ?? "codex", single)] : [];
        foreach (var (bucketId, bucket) in buckets)
        {
            if (bucket.ValueKind != JsonValueKind.Object) { continue; }
            var label = CodexUsageProvider.Text(bucket, "limitName");
            plan ??= CodexUsageProvider.Text(bucket, "planType");
            foreach (var role in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(role, out var w) || w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("usedPercent", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var percent) || !double.IsFinite(percent)) { continue; }
                long? duration = w.TryGetProperty("windowDurationMins", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var minutes) && minutes > 0 ? minutes : null;
                DateTimeOffset? reset = null;
                if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var seconds)) { try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { } }
                var name = duration switch { 300 => "5-hour", 10080 => "Weekly", not null => string.Create(CultureInfo.InvariantCulture, $"{duration} min"), _ => role == "primary" ? "Current window" : "Secondary window" };
                if (bucketId != "codex") { name = (label ?? bucketId) + " · " + name; }
                var locked = CodexUsageProvider.Text(bucket, "rateLimitReachedType");
                windows.Add(UsageWindow.Create(bucketId + ":" + role, percent, reset, name, locked) with { DurationMinutes = duration });
            }
            if (bucketId == "codex" && bucket.TryGetProperty("credits", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                credits = c.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True ? "Unlimited credits" : CodexUsageProvider.Text(c, "balance") is { } balance ? balance + " credits available" : "Credit balance unavailable";
            }
        }
        if (limits.TryGetProperty("rateLimitResetCredits", out var resets) && resets.ValueKind == JsonValueKind.Object && resets.TryGetProperty("availableCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var n) && n >= 0) { credits = (credits is null ? string.Empty : credits + " · ") + n.ToString(CultureInfo.InvariantCulture) + " earned resets"; }
        var rawId = CodexUsageProvider.Text(limits, "accountId") ?? CodexUsageProvider.Text(account, "email");
        var accountKey = rawId is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId)));
        return new UsageSnapshot(windows, plan, CodexUsageProvider.Text(account, "email"), null, now, UsageSource.Live) { AccountKey = accountKey, CreditsText = credits };
    }
}

public sealed class DemoUsageProvider : IUsageProvider
{
    public string Name => "Synthetic capture";
    public Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(UsageFetchResult.Success(new UsageSnapshot([
            UsageWindow.Create("codex:primary", 59, now.AddHours(3), "5-hour") with { DurationMinutes = 300 },
            UsageWindow.Create("codex:secondary", 16, now.AddDays(4), "Weekly") with { DurationMinutes = 10080 },
        ], "pro", null, null, now, UsageSource.Live) { AccountKey = "synthetic", CreditsText = "240 credits available" }));
    }
}
