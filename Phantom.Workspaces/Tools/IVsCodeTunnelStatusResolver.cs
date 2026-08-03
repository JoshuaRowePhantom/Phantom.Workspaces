namespace Phantom.Workspaces.Tools;

/// <summary>
/// Resolves the currently-running VS Code dev tunnel by invoking <c>code tunnel status</c> and
/// parsing its stdout. Returns <see langword="null"/> when the CLI reports that no tunnel is
/// running (or when the output cannot be parsed) rather than throwing.
/// </summary>
public interface IVsCodeTunnelStatusResolver
{
    Task<VsCodeTunnelStatus?> GetTunnelStatusAsync(string cliPath, CancellationToken cancellationToken);
}
