using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tools;

public sealed record VsCodeTunnelStartRequest
{
    public required string CliPath { get; init; }
    public required string TunnelName { get; init; }
    public required VsCodeTunnelProvider Provider { get; init; }
    public required AccessRequestor Requestor { get; init; }
}
