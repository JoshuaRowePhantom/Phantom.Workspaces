using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm;

public struct CreateAgentChatRequest
{
    public string? AgentSessionId { get; init; }

    public AgentDefinition? AgentDefinition { get; init; }

    public AgentServices? AgentServices { get; init; }

    /// <summary>
    /// Optional trust profile provider. When set and the agent definition references a trust
    /// profile (via <c>Metadata["trust-profile"]</c>) that does not permit local execution,
    /// construction fails.
    /// </summary>
    public ITrustProfileProvider? TrustProfileProvider { get; init; }
}
