namespace Phantom.Workspaces.Tools;

public enum VsCodeTunnelOutcome
{
    Ready,
    NotRunning,
    AuthenticationRequired,
    RunningDisconnected,
    CommandFailed,
    InvalidStatus,
    Cancelled,
}

public sealed record VsCodeTunnelResolution
{
    public required VsCodeTunnelOutcome Outcome { get; init; }

    public VsCodeTunnelStatus? Status { get; init; }
}
