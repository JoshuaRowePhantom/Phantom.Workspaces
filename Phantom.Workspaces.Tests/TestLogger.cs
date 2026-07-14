using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Tests;

internal sealed class TestLogger<T> : ILogger<T>
{
    public sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        this.Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }
}
