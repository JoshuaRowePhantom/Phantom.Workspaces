using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Represents one "agent-definition" tool entry from the dispatcher's manifest.
/// Declares one available sub-agent template.
/// </summary>
public sealed class AgentDefinitionTool
{
    /// <summary>The definition ID, referenced as the first token in new(id) or new(id subagent-id).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description, used in completions and /available-subagents output.</summary>
    public required string Description { get; init; }

    /// <summary>The resolved AgentDefinition (from inline definition or manifest-reference).</summary>
    public required AgentDefinition Definition { get; init; }
}
