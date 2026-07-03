using AgentSchema;
using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public interface IRunningAgentChatFactory
{
    /// <summary>
    /// The live set of sessions currently held by at least one lease.
    /// Mutations are dispatched on the foreground scheduler; UI subscribers need not marshal.
    /// </summary>
    ObservableCollection<RunningAgentChat> RunningSessions { get; }

    /// <summary>
    /// Acquires a ref-counted lease on the AgentChat for <paramref name="sessionId"/>,
    /// loading it from persistence if not already running.
    /// </summary>
    Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new AgentChat from <paramref name="definition"/> + <paramref name="sessionId"/>,
    /// persists it, adds to RunningSessions, and returns a lease.
    /// </summary>
    Task<RunningAgentChatLease> CreateAsync(
        AgentDefinition definition,
        AgentSessionId sessionId,
        AgentServices? services = null,
        CancellationToken ct = default);
}
