using System;
using System.IO;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// Resolves a log directory for hosts that are <b>not</b> the main <c>Phantom.Workspaces.exe</c>
/// process and therefore cannot resolve their log directory through the
/// <c>WorkspacesConfiguration</c>-based single-resolution path introduced in #1086 — namely the
/// standalone <c>Phantom.Workspaces.Web.Server</c> and <c>Phantom.Workspaces.Agent.Cli</c>
/// executables and test hosts (#1095).
/// </summary>
/// <remarks>
/// Resolution order (first match wins):
/// <list type="number">
/// <item>an explicit directory supplied by the caller (e.g. a command-line switch);</item>
/// <item>the <see cref="LogDirectoryEnvironmentVariable"/> environment variable, for deployment
/// flexibility;</item>
/// <item>a <c>logs</c> directory beneath the supplied base directory (the host content root or the
/// executable base directory).</item>
/// </list>
/// None of these read <c>WorkspacesConfiguration</c>, keeping #1086's single-resolution invariant
/// intact for the main <c>.exe</c>. The resolved directory is created on disk before it is returned.
/// </remarks>
public static class HostLogDirectoryResolver
{
    /// <summary>
    /// Environment variable consulted for an explicit log directory when the caller does not supply
    /// one directly.
    /// </summary>
    public const string LogDirectoryEnvironmentVariable = "PHANTOM_WORKSPACES_LOG_DIRECTORY";

    /// <summary>The default name of the log directory created beneath the base directory.</summary>
    public const string DefaultLogDirectoryName = "logs";

    /// <summary>
    /// Resolves and creates the log directory for a config-less host.
    /// </summary>
    /// <param name="baseDirectory">
    /// The host-specific root used when no explicit directory or environment override is present
    /// (for example an <c>IHostEnvironment.ContentRootPath</c> or <see cref="AppContext.BaseDirectory"/>).
    /// </param>
    /// <param name="explicitDirectory">
    /// An explicit directory (for example from a command-line switch); when non-empty it wins.
    /// </param>
    /// <param name="environmentReader">
    /// Reads environment variables; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// Overridable so tests never depend on ambient process environment.
    /// </param>
    public static string Resolve(
        string baseDirectory,
        string? explicitDirectory = null,
        Func<string, string?>? environmentReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        environmentReader ??= Environment.GetEnvironmentVariable;

        string directory;
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            directory = explicitDirectory!;
        }
        else
        {
            var fromEnvironment = environmentReader(LogDirectoryEnvironmentVariable);
            directory = string.IsNullOrWhiteSpace(fromEnvironment)
                ? Path.Combine(baseDirectory, DefaultLogDirectoryName)
                : fromEnvironment!;
        }

        Directory.CreateDirectory(directory);
        return directory;
    }
}
