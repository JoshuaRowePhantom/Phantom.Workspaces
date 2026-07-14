using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Wrapper for a child <see cref="AgentChat"/> that was registered via <see cref="ISubAgentTable.Add"/>
/// or restored lazily from persistence via <see cref="IRunningAgentChatFactory"/>.
/// Implements <see cref="IRunningSubAgent"/> so it can appear in the parent's
/// <see cref="AgentChat.SubAgents"/> observable collection.
/// </summary>
public sealed class SubAgent : IRunningSubAgent
{
    private readonly IRunningAgentChatFactory? _factory;

    /// <summary>The session ID of the child agent chat.</summary>
    public AgentSessionId SessionId { get; }

    /// <summary>
    /// The child agent chat this sub-agent wraps.
    /// Non-null on the eager path (registered via <see cref="ISubAgentTable.Add"/>);
    /// null on the lazy path until <see cref="AcquireLeaseAsync"/> is called.
    /// </summary>
    internal AgentChat? AgentChat { get; }

    /// <summary>Eager path — AgentChat already in hand (from <see cref="ISubAgentTable.Add"/>).</summary>
    internal SubAgent(AgentSessionId sessionId, AgentChat agentChat, IRunningAgentChatFactory? factory)
    {
        SessionId = sessionId;
        AgentChat = agentChat;
        _factory = factory;
    }

    /// <summary>Lazy path — AgentChat not yet loaded (from RestoreSubAgentsAsync).</summary>
    internal SubAgent(AgentSessionId sessionId, IRunningAgentChatFactory? factory)
    {
        SessionId = sessionId;
        AgentChat = null;
        _factory = factory;
    }

    /// <summary>
    /// Acquires a ref-counted lease on the child <see cref="AgentChat"/> via the
    /// <see cref="IRunningAgentChatFactory"/>, loading it from persistence if not already running.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
    {
        var factory = _factory
            ?? throw new InvalidOperationException(
                "Cannot acquire a lease: IRunningAgentChatFactory is not available.");
        return factory.GetAsync(SessionId, ct);
    }

    string IRunningSubAgent.AgentId => AgentChat?.AgentId ?? SessionId.Value;
    string IRunningSubAgent.DisplayName => AgentChat?.DisplayName ?? SessionId.Value;
    string IRunningSubAgent.Description => AgentChat?.Description ?? string.Empty;
    AgentChatCompletionState IRunningSubAgent.CompletionState => AgentChat?.CompletionState ?? AgentChatCompletionState.Unknown;
    DateTime IRunningSubAgent.LastUpdatedAt => AgentChat?.LastUpdatedAt ?? DateTime.MinValue;
    IReadOnlyList<IRunningSubAgent> IRunningSubAgent.SubAgents => AgentChat?.SubAgents ?? (IReadOnlyList<IRunningSubAgent>)[];
}
