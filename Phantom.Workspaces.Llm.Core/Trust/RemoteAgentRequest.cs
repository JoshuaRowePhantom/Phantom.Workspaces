using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// The request contract for remote agent execution. Sent by the Workspaces remoting client
/// (<c>WebRemoteChatClient</c>) to a remote host's <c>POST /agent/respond</c> endpoint.
/// </summary>
public sealed record RemoteAgentRequest
{
    /// <summary>The agent definition JSON to execute remotely.</summary>
    public required string AgentDefinitionJson { get; init; }

    /// <summary>Optional remote agent session id.</summary>
    public string? AgentSessionId { get; init; }

    /// <summary>
    /// Optional target client instance (a <c>user-computer-profile</c> entity id). When set to a
    /// non-local instance that is currently connected to this host over a reverse tunnel, the host
    /// relays the turn back to that connected instance instead of running the agent locally.
    /// <c>null</c>, empty, or <see cref="TrustProfile.LocalClientInstance"/> mean run locally.
    /// </summary>
    public string? TargetClientInstance { get; init; }

    /// <summary>The conversation messages for this turn.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Optional composed <see cref="TrustProfile"/> content (JSON) supplied by the caller. When present,
    /// the remote host enforces the profile's tool-call policy on the agent's tools (the caller is
    /// trusted to provide the effective profile; see docs/design/trust-execution-open-questions.md).
    /// </summary>
    public string? TrustProfileJson { get; init; }
}
