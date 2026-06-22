using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;

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

    public AgentServices? AgentServices { get; init; }

    /// <summary>
    /// Optional trust profile provider. When set and the agent definition references a trust
    /// profile (via <c>Metadata["trust-profile"]</c>) that does not permit local execution,
    /// construction fails.
    /// </summary>
    public ITrustProfileProvider? TrustProfileProvider { get; init; }
}
