namespace Phantom.Workspaces.Tools;

/// <summary>
/// A parsed snapshot of the VS Code dev tunnel currently reported by <c>code tunnel status</c>.
/// A non-null <see cref="VsCodeTunnelStatus"/> means the tunnel daemon is running (the outer
/// <c>tunnel</c> member of the status JSON is non-null); <see cref="IsConnected"/> reflects the
/// inner <c>tunnel</c> health string (<c>"Connected"</c>). <see cref="LastFailReason"/> carries the
/// daemon's last failure reason when reported (typically while running-but-disconnected).
/// </summary>
public sealed record VsCodeTunnelStatus(
    string TunnelName,
    string TunnelUrl,
    bool IsConnected,
    string? LastFailReason = null);
