namespace Phantom.Workspaces.Services.Logging;

/// <summary>
/// The single source of truth for the resolved log directory within this process. Every consumer
/// (the GUI startup path, the embedded <see cref="WorkspacesWebHost"/>, and the rolling-file
/// provider) obtains the log directory from this one object; nothing else computes a log path.
/// </summary>
public interface ILogDirectoryProvider
{
    /// <summary>The resolved, created-on-demand log directory for this process.</summary>
    string LogDirectory { get; }
}
