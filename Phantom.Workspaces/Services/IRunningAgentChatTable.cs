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
    /// Acquires (or joins) a running agent-chat session identified by <see cref="AcquireAgentChatRequest.AgentSessionId"/>.
    /// When the request resolves a definition and the session is not yet running, the
    /// definition is persisted and a new <see cref="AgentChat"/> is created. When the session is
    /// already running, the existing <see cref="AgentChat"/> is returned.
    /// Registers request entity metadata for display in
    /// <see cref="RunningSessions"/> if this is the first caller for the session.
    /// <see cref="AcquireAgentChatRequest.EntityDisplayName"/> and <see cref="AcquireAgentChatRequest.EntityDescription"/> are used to
    /// populate the <see cref="AgentChat.DisplayName"/> and <see cref="AgentChat.Description"/>
    /// properties when creating a new session.
    /// Dispose the returned lease when done; the underlying <see cref="AgentChat"/> is disposed when
    /// the last lease is released.
    /// </summary>
    Task<RunningAgentChatLease> AcquireAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct = default);
}

