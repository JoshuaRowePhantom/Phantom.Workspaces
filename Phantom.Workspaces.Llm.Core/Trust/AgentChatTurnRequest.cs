using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// The request contract for a single stateful agent turn sent to
/// <c>POST /agent/chat/{sessionId}/turn</c>. Unlike <see cref="RemoteAgentRequest"/>, this sends
/// only the latest user message(s) — the remote <see cref="AgentChatSessionCache"/> owns the
/// authoritative conversation history and the stateful <see cref="IChatClient"/> session.
/// </summary>
public sealed record AgentChatTurnRequest
{
    /// <summary>The agent definition JSON identifying the provider and tools to use.</summary>
    public required string AgentDefinitionJson { get; init; }

    /// <summary>
    /// The shared session identifier. The remote host maps this to a cached
    /// <see cref="AgentChat"/> that persists across turns.
    /// </summary>
    public required string AgentSessionId { get; init; }

    /// <summary>The latest user message(s) for this turn.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
}
