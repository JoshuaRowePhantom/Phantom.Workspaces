using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Request for projecting an <see cref="AgentManifest"/> into a concrete
/// <see cref="AgentDefinition"/> by resolving the manifest's tool resources.
/// </summary>
public struct CreateAgentDefinitionRequest
{
    /// <summary>
    /// The manifest to project. Its <see cref="AgentManifest.Template"/> provides the base
    /// agent definition and its resources provide the tool resources to resolve and append.
    /// </summary>
    public AgentManifest AgentManifest { get; init; }

    /// <summary>
    /// The factory used to resolve each tool resource into a concrete tool.
    /// </summary>
    public IToolResourceFactory? ToolResourceFactory { get; init; }

    /// <summary>
    /// Parameter values to substitute into the manifest template before resolving tool resources.
    /// Keys are parameter names; values are their resolved string representations.
    /// Only used when <see cref="AgentManifest"/> is provided.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}
