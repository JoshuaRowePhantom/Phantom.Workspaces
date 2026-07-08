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
        => _factory.GetAsync(SessionId, ct);
}
