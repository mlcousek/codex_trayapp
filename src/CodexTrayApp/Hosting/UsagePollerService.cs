using System.Diagnostics;
using CodexTrayApp.Core.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Hosting;

/// <summary>Runs the polling loop for the lifetime of the host.</summary>
internal sealed class UsagePollerService : BackgroundService
{
    private readonly UsagePoller _poller;
    private readonly ILogger<UsagePollerService> _logger;

    public UsagePollerService(UsagePoller poller, ILogger<UsagePollerService> logger)
    {
        _poller = poller;
        _logger = logger;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Usage poller stopped in {Elapsed} ms", stopwatch.ElapsedMilliseconds);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _poller.RunAsync(stoppingToken);
}
