using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Covers #1093: the centralized <see cref="GlobalExceptionLogging"/> helper subscribed by every host
/// entry point. Tests mutate process-global static state, so each resets the helper before and after.
/// </summary>
public sealed class GlobalExceptionLoggingTests : IDisposable
{
    public GlobalExceptionLoggingTests() => GlobalExceptionLogging.ResetForTests();

    public void Dispose() => GlobalExceptionLogging.ResetForTests();

    [Fact]
    public void GlobalExceptionLogging_Register_SubscribesAllGlobalHandlers()
    {
        var factory = new CapturingLoggerFactory();

        GlobalExceptionLogging.Register(factory);

        Assert.True(GlobalExceptionLogging.IsRegistered);

        // Each global source routes to the injected logger once registered.
        GlobalExceptionLogging.OnAppDomainUnhandledException(
            null,
            new UnhandledExceptionEventArgs(new InvalidOperationException("appdomain"), isTerminating: false));
        GlobalExceptionLogging.OnUnobservedTaskException(
            null,
            new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("unobserved"))));
        GlobalExceptionLogging.OnDispatcherUnhandled(new InvalidOperationException("dispatcher"));

        Assert.Equal(3, factory.Logger.Entries.Count);
    }

    [Fact]
    public void GlobalExceptionLogging_UnobservedTaskException_IsLoggedViaCapturingLogger()
    {
        var factory = new CapturingLoggerFactory();
        GlobalExceptionLogging.Register(factory);

        var fault = new InvalidOperationException("unobserved fault");
        GlobalExceptionLogging.OnUnobservedTaskException(
            null,
            new UnobservedTaskExceptionEventArgs(new AggregateException(fault)));

        var entry = Assert.Single(factory.Logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Same(fault, entry.Exception);
    }

    [Fact]
    public void GlobalExceptionLogging_UnobservedTaskException_IsObservedToPreventFinalizerCrash()
    {
        var factory = new CapturingLoggerFactory();
        GlobalExceptionLogging.Register(factory);

        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("boom")));
        GlobalExceptionLogging.OnUnobservedTaskException(null, args);

        Assert.True(args.Observed);
    }

    [Fact]
    public void GlobalExceptionLogging_AppDomainUnhandledException_LogsFullStackTrace()
    {
        var factory = new CapturingLoggerFactory();
        GlobalExceptionLogging.Register(factory);

        var thrown = CreateExceptionWithStackTrace();
        GlobalExceptionLogging.OnAppDomainUnhandledException(
            null,
            new UnhandledExceptionEventArgs(thrown, isTerminating: true));

        var entry = Assert.Single(factory.Logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Same(thrown, entry.Exception);
        Assert.NotNull(entry.Exception!.StackTrace);
        Assert.Contains("IsTerminating=True", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalExceptionLogging_AggregateException_IsFlattenedInLogOutput()
    {
        var factory = new CapturingLoggerFactory();
        GlobalExceptionLogging.Register(factory);

        var first = new InvalidOperationException("first inner");
        var second = new ArgumentException("second inner");
        var aggregate = new AggregateException(new AggregateException(first, second));

        GlobalExceptionLogging.OnAppDomainUnhandledException(
            null,
            new UnhandledExceptionEventArgs(aggregate, isTerminating: false));

        Assert.Equal(2, factory.Logger.Entries.Count);
        Assert.Contains(factory.Logger.Entries, e => ReferenceEquals(e.Exception, first));
        Assert.Contains(factory.Logger.Entries, e => ReferenceEquals(e.Exception, second));
    }

    [Fact]
    public void GlobalExceptionLogging_Register_CalledTwice_IsIdempotent()
    {
        var first = new CapturingLoggerFactory();
        var second = new CapturingLoggerFactory();

        GlobalExceptionLogging.Register(first);
        GlobalExceptionLogging.Register(second);

        // The first call wins: the second factory is ignored entirely.
        Assert.Equal(0, second.CreateLoggerCallCount);

        GlobalExceptionLogging.OnDispatcherUnhandled(new InvalidOperationException("boom"));

        Assert.Single(first.Logger.Entries);
        Assert.Empty(second.Logger.Entries);
    }

    [Fact]
    public void GlobalExceptionLogging_DispatcherUnhandledException_IsLogged()
    {
        var factory = new CapturingLoggerFactory();
        GlobalExceptionLogging.Register(factory);

        var fault = new InvalidOperationException("dispatcher fault");
        GlobalExceptionLogging.OnDispatcherUnhandled(fault);

        var entry = Assert.Single(factory.Logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(fault, entry.Exception);
    }

    private static Exception CreateExceptionWithStackTrace()
    {
        try
        {
            throw new InvalidOperationException("with stack");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public CapturingLogger Logger { get; } = new();

        public int CreateLoggerCallCount { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            this.CreateLoggerCallCount++;
            return this.Logger;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
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
}
