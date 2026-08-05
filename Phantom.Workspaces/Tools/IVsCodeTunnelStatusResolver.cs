namespace Phantom.Workspaces.Tools;

/// <summary>
/// Resolves the currently-running VS Code dev tunnel by invoking <c>code tunnel status</c> and
/// parsing its stdout. Returns a <see cref="VsCodeTunnelResolution"/> that carries both the
/// parsed <see cref="VsCodeTunnelStatus"/> (when a tunnel is running) AND the captured CLI
/// output (for logging / user-facing reporting).
/// </summary>
public interface IVsCodeTunnelStatusResolver
{
    Task<VsCodeTunnelResolution> ResolveAsync(string cliPath, CancellationToken cancellationToken);
}
