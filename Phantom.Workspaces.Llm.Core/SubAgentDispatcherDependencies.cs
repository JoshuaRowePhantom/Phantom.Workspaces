using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// The runtime dependencies required to construct a <see cref="SubAgentDispatcherChatClient"/> from
/// <see cref="AgentFactory"/>.
/// </summary>
/// <remarks>
/// <see cref="IChatClient"/> implementations do not have access to their own
/// <see cref="AgentSchema.AgentDefinition"/> at construction time; <see cref="AgentFactory"/> unpacks
/// the fields each client needs. The dispatcher additionally needs services that
/// <c>Phantom.Workspaces.Llm.Interfaces.AgentServices</c> cannot carry (it cannot reference
/// <c>Data.Core</c> types such as <see cref="IEmbeddingsProvider"/>, <see cref="IDataAccessLayer"/>,
/// <see cref="EntityName"/> or <see cref="AgentDefinitionTool"/>), so those are supplied here.
/// The <c>agent-definition</c> tool entries are resolved by the caller (via
/// <c>AgentDefinitionToolExtractor</c> + <c>AgentDefinitionResolver</c> in the main project) before
/// being passed in.
/// </remarks>
public sealed class SubAgentDispatcherDependencies
{
    /// <summary>
    /// Factory used to lease running sub-agent chat sessions. When <see langword="null"/>,
    /// <see cref="AgentFactory"/> falls back to <c>AgentServices.RunningAgentChatFactory</c>.
    /// </summary>
    public IRunningAgentChatFactory? RunningAgentChatFactory { get; init; }

    /// <summary>Embeddings provider used for fuzzy routing of sub-agents.</summary>
    public required IEmbeddingsProvider EmbeddingsProvider { get; init; }

    /// <summary>Data access layer used to persist and restore sub-agent entities.</summary>
    public IDataAccessLayer? DataAccessLayer { get; init; }

    /// <summary>The dispatcher session's entity name; sub-agent entity names are derived from it.</summary>
    public required EntityName DispatcherEntityName { get; init; }

    /// <summary>The resolved sub-agent templates available to the dispatcher.</summary>
    public required IReadOnlyList<AgentDefinitionTool> AgentDefinitionTools { get; init; }
}
