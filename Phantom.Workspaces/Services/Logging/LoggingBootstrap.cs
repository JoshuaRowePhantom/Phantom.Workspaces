using System;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// Centralizes construction of the process <see cref="ILoggerFactory"/> so that every entry point
/// (the GUI startup path and the embedded <see cref="WorkspacesWebHost"/>) builds a factory backed
/// by the same <see cref="RollingFileLoggerProvider"/> writing to the one
/// <see cref="ILogDirectoryProvider.LogDirectory"/>. No caller computes a log directory or wires a
/// file provider independently.
/// </summary>
public static class LoggingBootstrap
{
    /// <summary>The retention window applied to rolling log files.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> that writes through a rolling file provider rooted at
    /// the single <paramref name="logDirectoryProvider"/> directory.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(ILogDirectoryProvider logDirectoryProvider)
    {
        ArgumentNullException.ThrowIfNull(logDirectoryProvider);

        var directory = logDirectoryProvider.LogDirectory;
        return LoggerFactory.Create(builder =>
            builder.AddProvider(new RollingFileLoggerProvider(directory, DefaultRetention)));
    }
}
