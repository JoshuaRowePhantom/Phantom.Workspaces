namespace Phantom.Workspaces.Tools;

/// <summary>
/// A parsed snapshot of the VS Code dev tunnel currently reported by <c>code tunnel status</c>.
/// </summary>
public sealed record VsCodeTunnelStatus(
    string TunnelName,
    string TunnelUrl,
    bool IsConnected);
