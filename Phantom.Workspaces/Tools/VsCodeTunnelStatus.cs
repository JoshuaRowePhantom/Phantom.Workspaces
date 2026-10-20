namespace Phantom.Workspaces.Tools;

/// <summary>
/// A parsed snapshot of the VS Code dev tunnel currently reported by <c>code tunnel status</c>.
/// A non-null <see cref="VsCodeTunnelStatus"/> means the tunnel daemon is running (the outer
/// <c>tunnel</c> member of the status JSON is non-null); <see cref="IsConnected"/> reflects the
/// inner <c>tunnel</c> health string (<c>"Connected"</c>).
/// </summary>
public sealed record VsCodeTunnelStatus(
    string TunnelName,
    string TunnelUrl,
    bool IsConnected);
