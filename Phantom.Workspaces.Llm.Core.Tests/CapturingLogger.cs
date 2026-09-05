using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Minimal in-memory <see cref="ILogger"/> capturing (level, message) pairs so tests can assert on
/// logged OAuth request/result events and confirm secrets/tokens are never present (#1446/#1408).
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        this.Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// An <see cref="ILoggerFactory"/> that routes every created logger into a single shared
/// <see cref="Entries"/> list, letting tests inspect all messages emitted through a subsystem.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new SharedLogger(this.Entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class SharedLogger(List<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
