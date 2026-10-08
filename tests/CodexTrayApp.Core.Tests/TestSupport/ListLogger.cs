using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Tests.TestSupport;

/// <summary>Collects formatted log lines so tests can assert what was, and was not, logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public string All => string.Join(Environment.NewLine, Entries);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = string.Concat("[", logLevel.ToString(), "] ", formatter(state, exception));
        if (exception is not null)
        {
            line = string.Concat(line, " ", exception.ToString());
        }

        Entries.Add(line);
    }
}
