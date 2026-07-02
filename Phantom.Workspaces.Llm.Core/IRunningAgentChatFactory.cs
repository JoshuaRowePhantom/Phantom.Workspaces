using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Minimal stub for acquiring ref-counted leases on running agent chat sessions.
/// The full interface (RunningSessions, CreateAsync) is defined in #670.
/// </summary>
public interface IRunningAgentChatFactory
{
    /// <summary>
    /// Acquires a ref-counted lease on the AgentChat for <paramref name="sessionId"/>.
    /// Throws <see cref="ObjectDisposedException"/> or <see cref="InvalidOperationException"/>
    /// if the session has been evicted; never returns null.
    /// </summary>
    Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default);
}
