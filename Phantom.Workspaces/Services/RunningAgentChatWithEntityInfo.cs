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

    /// <summary>
    /// The owning workspace-pane id (the pane the session was started/opened in), if known.
    /// Used by cross-workspace status-button navigation (#1135) to switch to (and load) the
    /// owning workspace before focusing the agent, so a click on the brain popup never routes
    /// the agent into the currently-active pane by mistake.
    /// </summary>
    public string? WorkspaceId { get; }

    internal RunningAgentChatWithEntityInfo(RunningAgentChat chat, string entityName, string? entityId, string? workspaceId = null)
    {
        _chat = chat;
        EntityName = entityName;
        EntityId = entityId;
        WorkspaceId = workspaceId;
    }

    /// <summary>
    /// Acquires a new ref-counted lease on this session's <see cref="AgentChat"/>.
    /// Delegates to the underlying <see cref="RunningAgentChat.AcquireLeaseAsync"/>.
    /// Dispose the lease when done.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
        => _chat.AcquireLeaseAsync(ct);
}
