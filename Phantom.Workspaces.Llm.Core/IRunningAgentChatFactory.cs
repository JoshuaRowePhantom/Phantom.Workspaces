using AgentSchema;
using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public interface IRunningAgentChatFactory : Interfaces.IRunningAgentChatFactory
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

    /// <summary>
    /// Returns a lease on the already-running session, or — when <paramref name="definition"/>
    /// is non-null and the session is not yet running — persists the definition, creates the
    /// <see cref="AgentChat"/>, and returns a new lease. If <paramref name="definition"/> is
    /// <see langword="null"/> and the session is not running, delegates to
    /// <see cref="GetAsync"/> (which loads from persistence).
    /// <paramref name="displayNameOverride"/> and <paramref name="descriptionOverride"/> are used
    /// to populate the chat's DisplayName and Description when creating a new session.
    /// </summary>
    Task<RunningAgentChatLease> GetOrCreateAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null,
        AgentServices? services = null,
        string? displayNameOverride = null,
        string? descriptionOverride = null,
        CancellationToken ct = default);
}
