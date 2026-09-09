using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;
using ProtocolReplayCursor = Phantom.Workspaces.Llm.Remote.ReplayCursor;

namespace Phantom.Workspaces.Services.AgentSessions;

internal enum AgentSessionAuthorizationOperation
{
    Status, Open, Reconnect, Send, SetToolState, SetBackgroundPreference, Interrupt, Terminate,
    OpenSubagent, ModalResponse, Takeover,
}

internal sealed record AgentSessionAuthorizationRequest
{
    public required string AgentSessionId { get; init; }
    public required string ExpectedOwningProfileEntityId { get; init; }
    public required long ExpectedOwnershipGeneration { get; init; }
    public required AgentSessionAuthorizationOperation Operation { get; init; }
    public string? ChildAgentId { get; init; } = null;
    public string? NewOwningProfileEntityId { get; init; } = null;
}

internal readonly record struct AgentSessionAuthorizationDecision
{
    public required bool IsAllowed { get; init; }
}

internal sealed record OpenAgentSessionHostRequest
{
    public required TransportPeerIdentity Peer { get; init; }
    public required AgentSessionOpenRequest OpenRequest { get; init; }
    public required IMessageChannel Channel { get; init; }
}

internal sealed record TerminateAgentSessionRuntimeRequest
{
    public required string SessionId { get; init; }
    public required long OwnershipGeneration { get; init; }
    public required RuntimeEpoch Epoch { get; init; }
}

internal sealed record UpdateAgentSessionRuntimeRetentionRequest
{
    public required string SessionId { get; init; }
    public required long OwnershipGeneration { get; init; }
    public required RuntimeEpoch Epoch { get; init; }
    public required bool ContinueInBackground { get; init; }
}

internal sealed record AttachRemoteAgentSessionRequest
{
    public required string AttachmentToken { get; init; }
    public required IMessageChannel Channel { get; init; }
    public ProtocolReplayCursor? Cursor { get; init; } = null;
}
