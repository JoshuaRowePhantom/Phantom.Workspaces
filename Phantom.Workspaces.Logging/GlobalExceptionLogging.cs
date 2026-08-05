using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// The single centralized helper that subscribes the global uncaught/unobserved exception sources and
/// records them through the #1086 file-logging facility. Every process entry point (main GUI, Agent
/// GUI, Web Server, CLI) calls <see cref="Register(ILoggerFactory)"/> exactly once, passing the
/// logger factory appropriate to that host (the config-driven factory for the main <c>.exe</c>, or a
/// config-less <c>HostFileLoggerFactory</c> for the standalone hosts). Registration is idempotent, so
/// repeated or multi-entry-point calls never double-subscribe (#1093).
/// </summary>
public static class GlobalExceptionLogging
{
    private static int registered; // Interlocked guard: register-once, idempotent.
    private static ILogger? logger;

    /// <summary>
    /// Subscribes <see cref="AppDomain.UnhandledException"/> and
    /// <see cref="TaskScheduler.UnobservedTaskException"/> to log through the supplied factory. The
    /// first call wins; subsequent calls are no-ops so multiple entry points can call this safely.
    /// </summary>
    public static void Register(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (Interlocked.Exchange(ref registered, 1) != 0)
        {
            return;
        }

        logger = loggerFactory.CreateLogger("GlobalExceptionLogging");

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Logs an unhandled exception observed on a GUI framework dispatcher. Called from each GUI app's
    /// <c>Dispatcher.UIThread.UnhandledException</c> hook before the crash dialog / <c>Handled</c> is
    /// set.
    /// </summary>
    public static void OnDispatcherUnhandled(Exception? exception)
        => Log(LogLevel.Error, exception, "Unhandled exception on the UI dispatcher.");

    internal static bool IsRegistered => Volatile.Read(ref registered) != 0;

    internal static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => Log(
            LogLevel.Critical,
            e.ExceptionObject as Exception,
            $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}.");

    internal static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log(
            LogLevel.Critical,
            e.Exception,
            "Unobserved Task exception (would otherwise crash via the finalizer thread).");

        // Log-then-observe: prevents the finalizer from rethrowing and crashing the process (#1084).
        e.SetObserved();
    }

    // Flattens AggregateException so every inner exception + full stack is recorded; passes the
    // Exception object to ILogger.Log so the full stack trace (not just the message) is written.
    private static void Log(LogLevel level, Exception? exception, string message)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                logger?.Log(level, inner, "{Message}", message);
            }

            return;
        }

        logger?.Log(level, exception, "{Message}", message);
    }

    // Test seam: unsubscribes the global handlers and clears state so idempotency and per-handler
    // behavior can be exercised deterministically without leaking subscriptions across tests.
    internal static void ResetForTests()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        logger = null;
        Volatile.Write(ref registered, 0);
    }
}
