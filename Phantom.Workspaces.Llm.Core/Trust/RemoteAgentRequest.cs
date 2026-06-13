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

    /// <summary>The conversation messages for this turn.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
}
