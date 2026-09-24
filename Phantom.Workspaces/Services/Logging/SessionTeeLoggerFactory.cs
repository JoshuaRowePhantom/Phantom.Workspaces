using System;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// Sends a session's records to its own UI memory and the process logger, without taking
/// ownership of either sink. An attached tab may observe the memory sink without another tee.
/// </summary>
public sealed class SessionTeeLoggerFactory(
    ILoggerFactory processFactory,
    ObservableLoggerFactory sessionMemoryFactory) : ISessionScopedLoggerFactory
{
    public ILoggerFactory SessionMemoryFactory => sessionMemoryFactory;

    public ILoggerFactory CreateChildSessionFactory()
        => new SessionTeeLoggerFactory(processFactory, new ObservableLoggerFactory());

    public ILogger CreateLogger(string categoryName)
        => new TeeLogger(
            processFactory.CreateLogger(categoryName),
            sessionMemoryFactory.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider)
        => throw new NotSupportedException("Register providers on the process logger factory.");

    public void Dispose()
    {
        // The process factory belongs to the application; the memory factory belongs to the tab.
    }

    private sealed class TeeLogger(ILogger processLogger, ILogger memoryLogger) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => new TeeScope(processLogger.BeginScope(state), memoryLogger.BeginScope(state));

        public bool IsEnabled(LogLevel logLevel)
            => processLogger.IsEnabled(logLevel) || memoryLogger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (processLogger.IsEnabled(logLevel))
                processLogger.Log(logLevel, eventId, state, exception, formatter);
            if (memoryLogger.IsEnabled(logLevel))
                memoryLogger.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class TeeScope(IDisposable? processScope, IDisposable? memoryScope) : IDisposable
    {
        public void Dispose()
        {
            memoryScope?.Dispose();
            processScope?.Dispose();
        }
    }
}
