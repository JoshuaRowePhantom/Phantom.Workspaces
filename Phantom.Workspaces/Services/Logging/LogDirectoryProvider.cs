using System;
using System.IO;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// Resolves the process log directory in exactly one place, driven by
/// <see cref="WorkspacesConfiguration"/>. When the configuration does not set an explicit
/// <see cref="WorkspacesConfiguration.LogDirectory"/>, the default computed by
/// <see cref="ConfigurationPersistenceService.GetDefaultLogDirectoryPath"/> is used. The directory
/// is created on first access.
/// </summary>
public sealed class LogDirectoryProvider : ILogDirectoryProvider
{
    private readonly Lazy<string> resolved;

    public LogDirectoryProvider(WorkspacesConfiguration configuration, string? configurationPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        this.resolved = new Lazy<string>(() =>
        {
            var directory = string.IsNullOrWhiteSpace(configuration.LogDirectory)
                ? ConfigurationPersistenceService.GetDefaultLogDirectoryPath(configurationPath)
                : configuration.LogDirectory!;
            Directory.CreateDirectory(directory);
            return directory;
        });
    }

    /// <inheritdoc />
    public string LogDirectory => this.resolved.Value;
}
