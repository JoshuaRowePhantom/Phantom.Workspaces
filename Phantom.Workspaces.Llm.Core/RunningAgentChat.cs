using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Entry type for the observable collection of live agent chat sessions.
/// Observers call <see cref="AcquireLeaseAsync"/> to obtain a ref-counted lease.
/// </summary>
public sealed class RunningAgentChat
{
    private readonly IRunningAgentChatFactory _factory;

    public AgentSessionId SessionId { get; }

    /// <summary>
    /// <see langword="true"/> when the backing <see cref="AgentChat"/> is a sub-agent
    /// (has a non-null parent chat). Sub-agents must never be surfaced by the
    /// top-right "Running agents" flyout — see issue #1205.
    /// Under the current wiring (issue #1205 Fix 1) sub-agents opt out of registration
    /// at the factory, so this flag is <see langword="false"/> in production paths; the
    /// property is exposed as a belt-and-braces marker so consumers (and future code
    /// paths that intentionally register a child) can filter without acquiring a lease.
    /// </summary>
    public bool IsSubAgent { get; init; }

    internal RunningAgentChat(AgentSessionId sessionId, IRunningAgentChatFactory factory)
    {
        SessionId = sessionId;
        _factory = factory;
    }

    /// <summary>
    /// Acquires a new ref-counted lease on this session's AgentChat.
    /// Dispose the lease when done.
    /// </summary>
    public Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
        => _factory.GetAsync(SessionId, ct: ct);
}
