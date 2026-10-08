using CodexTrayApp.Core.Analytics;
using Microsoft.Extensions.Hosting;

namespace CodexTrayApp.Hosting;

/// <summary>Runs the session-log scanner and watcher for the lifetime of the host.</summary>
internal sealed class LocalAnalyticsService : BackgroundService
{
    private readonly LocalAnalyticsProvider _provider;

    public LocalAnalyticsService(LocalAnalyticsProvider provider)
    {
        _provider = provider;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _provider.RunAsync(stoppingToken);
}
