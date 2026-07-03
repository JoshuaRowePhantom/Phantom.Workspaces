using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Enriches a <see cref="RunningAgentChat"/> from <c>Llm.Core</c> with workspace entity display
/// information for presentation in the running-agent brain popup.
/// </summary>
public sealed class RunningAgentChatWithEntityInfo
{
    private readonly RunningAgentChat _chat;

    /// <summary>The agent session identifier.</summary>
    public AgentSessionId SessionId => _chat.SessionId;

    /// <summary>The display name of the agent entity that owns this session.</summary>
    public string EntityName { get; }

    /// <summary>
    /// The entity store ID (UUID) of the agent-session entity, if available.
    /// Used to navigate to the entity when no tab is open for this session.
    /// </summary>
    public string? EntityId { get; }

    internal RunningAgentChatWithEntityInfo(RunningAgentChat chat, string entityName, string? entityId)
    {
        _chat = chat;
        EntityName = entityName;
        EntityId = entityId;
    }

    /// <summary>
    /// Acquires a new ref-counted lease on this session's <see cref="AgentChat"/>.
    /// Delegates to the underlying <see cref="RunningAgentChat.AcquireLeaseAsync"/>.
    /// Dispose the lease when done.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
        => _chat.AcquireLeaseAsync(ct);
}
