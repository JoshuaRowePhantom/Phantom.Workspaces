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
    /// processing loop).  Capture <see cref="TaskScheduler.FromCurrentSynchronizationContext"/>
    /// on the UI thread and pass it here whenever the agent chat is constructed off the UI thread
    /// (e.g. inside <c>Task.Run</c>), so the foreground scheduler is always the UI scheduler
    /// regardless of which thread builds the request.
    /// </summary>
    public TaskScheduler? ForegroundScheduler { get; init; }
}
