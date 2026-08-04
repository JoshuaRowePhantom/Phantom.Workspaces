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
    private AgentChatCompletionState? _restoredCompletionState;

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
    /// Issue #1186: Records the restored completion state for a lazy sub-agent stub
    /// without materialising the child <see cref="AgentChat"/>. The state surfaces
    /// through <see cref="IRunningSubAgent.CompletionState"/> immediately, and is
    /// applied to the child chat lazily when <see cref="AcquireLeaseAsync"/> is
    /// eventually called by the UI. This replaces the prior eager materialisation
    /// path which forced a full <c>CreateChatClientAsync</c> during startup restore
    /// and hung the splash screen when a persisted sub-agent's
    /// <see cref="AgentDefinition"/> was empty (no <c>Model</c>).
    /// </summary>
    public void SetRestoredCompletionState(AgentChatCompletionState state)
    {
        _restoredCompletionState = state;
    }

    /// <summary>Test/inspect helper: the restored completion-state override, if any.</summary>
    internal AgentChatCompletionState? RestoredCompletionState => _restoredCompletionState;

    /// <summary>
    /// Acquires a ref-counted lease on the child <see cref="AgentChat"/> via the
    /// <see cref="IRunningAgentChatFactory"/>, loading it from persistence if not already running.
    /// Issue #1186: if <see cref="SetRestoredCompletionState"/> was called on this stub,
    /// applies that terminal state to the materialised child chat so a later
    /// <c>AddSubAgentSlotLazy</c>-driven lease sees the correct completion state.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
    {
        var factory = _factory
            ?? throw new InvalidOperationException(
                "Cannot acquire a lease: IRunningAgentChatFactory is not available.");
        // Issue #1205: sub-agents must never appear in the top-right "Running agents" popup.
        // The lazy restore path routed through GetAsync used to unconditionally register the
        // materialised child chat as a top-level entry, producing "No Open Tab" pollution rows
        // for every persisted sub-agent after a GUI restart. Mirror the opt-out that #1150 added
        // to the live-creation path (GetOrCreateAsync).
        var leaseTask = factory.GetAsync(SessionId, registerAsRunningAgent: false, ct);
        var overrideState = _restoredCompletionState;
        if (overrideState is null)
        {
            // Fast path: no override to apply — return the factory task directly so
            // continuations schedule identically to the pre-#1186 behaviour and tests
            // that verify scheduler ordering keep working.
            return leaseTask;
        }
        return ApplyRestoredCompletionStateAsync(leaseTask, overrideState.Value);

        static async Task<RunningAgentChatLease> ApplyRestoredCompletionStateAsync(
            Task<RunningAgentChatLease> pending,
            AgentChatCompletionState state)
        {
            var lease = await pending.ConfigureAwait(false);
            if (lease.AgentChat is { } agentChat)
            {
                agentChat.SetCompletionState(state, preserveLastUpdatedAt: true);
            }
            return lease;
        }
    }

    string IRunningSubAgent.AgentId => AgentChat?.AgentId ?? SessionId.Value;
    string IRunningSubAgent.DisplayName => AgentChat?.DisplayName ?? SessionId.Value;
    string IRunningSubAgent.Description => AgentChat?.Description ?? string.Empty;
    string IRunningSubAgent.Name => AgentChat?.Name ?? string.Empty;
    AgentChatCompletionState IRunningSubAgent.CompletionState =>
        AgentChat?.CompletionState
        ?? _restoredCompletionState
        ?? AgentChatCompletionState.Unknown;
    DateTime IRunningSubAgent.LastUpdatedAt => AgentChat?.LastUpdatedAt ?? DateTime.MinValue;
    IReadOnlyList<IRunningSubAgent> IRunningSubAgent.SubAgents => AgentChat?.SubAgents ?? (IReadOnlyList<IRunningSubAgent>)[];
}
