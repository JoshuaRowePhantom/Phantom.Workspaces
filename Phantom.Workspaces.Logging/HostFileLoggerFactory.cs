using System;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// Builds an <see cref="ILoggerFactory"/> backed by the shared <see cref="RollingFileLoggerProvider"/>
/// (7-day retention by default) for hosts outside the main <c>WorkspacesConfiguration</c> path — the
/// standalone <c>Web.Server</c> and <c>Agent.Cli</c> executables and test hosts (#1095). The target
/// directory is resolved by <see cref="HostLogDirectoryResolver"/>; this helper never computes one
/// itself.
/// </summary>
public static class HostFileLoggerFactory
{
    /// <summary>The retention window applied to rolling log files, matching the #1086 facility.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> that writes through a
    /// <see cref="RollingFileLoggerProvider"/> rooted at <paramref name="logDirectory"/>.
    /// </summary>
    public static ILoggerFactory Create(string logDirectory, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var effectiveRetention = retention ?? DefaultRetention;
        return LoggerFactory.Create(builder =>
            builder.AddProvider(new RollingFileLoggerProvider(logDirectory, effectiveRetention)));
    }
}
