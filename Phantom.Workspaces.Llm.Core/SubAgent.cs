using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Wrapper for a child <see cref="AgentChat"/> that was registered via <see cref="ISubAgentTable.Add"/>.
/// Implements <see cref="IRunningSubAgent"/> so it can appear in the parent's
/// <see cref="AgentChat.SubAgents"/> observable collection alongside older-path sub-agents.
/// </summary>
public sealed class SubAgent : IRunningSubAgent
{
    private readonly Interfaces.IRunningAgentChatFactory? _factoryBase;

    /// <summary>The session ID of the child agent chat.</summary>
    public AgentSessionId SessionId { get; }

    /// <summary>The child agent chat this sub-agent wraps.</summary>
    public AgentChat AgentChat { get; }

    internal SubAgent(AgentSessionId sessionId, AgentChat agentChat, Interfaces.IRunningAgentChatFactory? factory)
    {
        SessionId = sessionId;
        AgentChat = agentChat;
        _factoryBase = factory;
    }

    /// <summary>
    /// Acquires a ref-counted lease on the child <see cref="AgentChat"/> via the
    /// <see cref="IRunningAgentChatFactory"/> that originally created it.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
    {
        var factory = (_factoryBase as IRunningAgentChatFactory)
            ?? throw new InvalidOperationException(
                "Cannot acquire a lease: IRunningAgentChatFactory is not available on the parent AgentServices.");
        return factory.GetAsync(SessionId, ct);
    }

    string IRunningSubAgent.AgentId => AgentChat.AgentId;
    string IRunningSubAgent.DisplayName => AgentChat.DisplayName;
    AgentChatCompletionState IRunningSubAgent.CompletionState => AgentChat.CompletionState;
    DateTime IRunningSubAgent.LastUpdatedAt => AgentChat.LastUpdatedAt;
    IReadOnlyList<IRunningSubAgent> IRunningSubAgent.SubAgents => AgentChat.SubAgents;
}
