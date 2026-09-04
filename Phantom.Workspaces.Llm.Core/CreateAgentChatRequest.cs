using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm;

public struct CreateAgentChatRequest
{
    public string? AgentSessionId { get; init; }

    public AgentDefinition? AgentDefinition { get; init; }

    /// <summary>
    /// Optional agent manifest. When set, it is projected into an <see cref="AgentDefinition"/>
    /// (resolving its tool resources via <see cref="ToolResourceFactory"/>) and used in preference
    /// to <see cref="AgentDefinition"/>.
    /// </summary>
    public AgentManifest? AgentManifest { get; init; }

    /// <summary>
    /// Factory used to resolve the tool resources referenced by <see cref="AgentManifest"/>.
    /// Required when <see cref="AgentManifest"/> references tool resources.
    /// </summary>
    public IToolResourceFactory? ToolResourceFactory { get; init; }

    /// <summary>
    /// Parameter values to substitute into the agent manifest template before resolving tool resources.
    /// Only used when <see cref="AgentManifest"/> is set.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    public AgentServices? AgentServices { get; init; }

    /// <summary>
    /// Optional trust profile provider. When set and the agent definition references a trust
    /// profile (via <c>Metadata["trust-profile"]</c>) that does not permit local execution,
    /// construction fails.
    /// </summary>
    public ITrustProfileProvider? TrustProfileProvider { get; init; }

    /// <summary>
    /// The scheduler used to run UI-bound work (history mutations, running-item updates, the
    /// processing loop). Chat construction and initialization must occur <em>on</em> the
    /// foreground context: when a <see cref="SynchronizationContextTaskScheduler"/> is supplied,
    /// the <see cref="AgentChat"/> constructor verifies the creating thread is on that context
    /// and throws otherwise (issue #909). Capture
    /// <see cref="SynchronizationContextTaskScheduler.FromCurrent"/> on the UI thread and create
    /// the chat on the UI thread; never construct the chat inside <c>Task.Run</c>.
    /// </summary>
    public TaskScheduler? ForegroundScheduler { get; init; }

    /// <summary>
    /// Cancellation token to observe during chat creation. Passed to the persistence store
    /// factory and other async operations within <see cref="AgentFactory.CreateAgentChatAsync"/>.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Optional time source used to stamp chat-history timestamps and <c>LastUpdatedAt</c> values,
    /// and to drive time-based waits. When <see langword="null"/>,
    /// <see cref="System.TimeProvider.System"/> is used. Tests inject a fake provider so that
    /// timestamp-ordered behaviour (e.g. the sub-agent tree ordering in issue #1226) is
    /// deterministic and does not depend on OS clock resolution. The provider flows to sub-agents
    /// created via <c>AgentChat.GetOrCreateAsync</c>.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Factory used to create the agent chat's persistence store. This delegate is invoked
    /// for every chat, including chats whose agent definition does not declare a
    /// <c>chat-history</c> tool — in that case the delegate is called with a <see langword="null"/>
    /// <see cref="ChatHistoryProviderDefinition"/> and is expected to return an in-memory store.
    /// When left <see langword="null"/>, <see cref="AgentFactory"/> uses a default delegate that
    /// maps a null definition to <see cref="AgentPersistenceStoreFactory.CreateInMemory"/> and a
    /// non-null definition to <see cref="AgentPersistenceStoreFactory.CreateAsync"/>.
    /// Intended primarily for testing; production callers can leave it null.
    /// </summary>
    public Func<ChatHistoryProviderDefinition?, CancellationToken, ValueTask<IAgentPersistenceStore>>? PersistenceStoreFactory { get; init; }
}
