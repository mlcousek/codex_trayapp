using CodexTrayApp.Core.History;
using CodexTrayApp.Core.Polling;
using Microsoft.Extensions.Hosting;

namespace CodexTrayApp.Hosting;

/// <summary>Wires the history recorder to the poller so every fresh snapshot lands in the time series.</summary>
internal sealed class HistoryRecorderService : IHostedService
{
    private readonly HistoryRecorder _recorder;
    private readonly UsagePoller _poller;

    public HistoryRecorderService(HistoryRecorder recorder, UsagePoller poller)
    {
        _recorder = recorder;
        _poller = poller;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recorder.Attach(_poller);
        _recorder.Record(_poller.Status);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _recorder.Dispose();
        return Task.CompletedTask;
    }
}
