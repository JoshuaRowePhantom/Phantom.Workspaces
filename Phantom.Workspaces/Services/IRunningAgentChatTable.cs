using System.Collections.ObjectModel;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

public interface IRunningAgentChatTable
{
    /// <summary>
    /// The live collection of active sessions, enriched with workspace entity display info.
    /// Mutations are dispatched on the foreground scheduler (mirrored from
    /// <see cref="IRunningAgentChatFactory.RunningSessions"/>); UI subscribers need not marshal.
    /// </summary>
    ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; }

    /// <summary>
    /// Acquires (or joins) a running agent-chat session identified by <paramref name="sessionId"/>.
    /// When <paramref name="definition"/> is non-null and the session is not yet running, the
    /// definition is persisted and a new <see cref="AgentChat"/> is created. When the session is
    /// already running, the existing <see cref="AgentChat"/> is returned.
    /// Registers <paramref name="entityName"/> / <paramref name="entityId"/> for display in
    /// <see cref="RunningSessions"/> if this is the first caller for the session.
    /// <paramref name="entityDisplayName"/> and <paramref name="entityDescription"/> are used to
    /// populate the <see cref="AgentChat.DisplayName"/> and <see cref="AgentChat.Description"/>
    /// properties when creating a new session.
    /// Dispose the returned lease when done; the underlying <see cref="AgentChat"/> is disposed when
    /// the last lease is released.
    /// </summary>
    Task<RunningAgentChatLease> AcquireAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null,
        AgentServices? agentServices = null,
        string entityName = "",
        string? entityId = null,
        string? entityDisplayName = null,
        string? entityDescription = null,
        CancellationToken ct = default);
}

