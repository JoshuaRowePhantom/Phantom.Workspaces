using AgentSchema;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Bundles an agent definition rewritten to opaque secret-reference tokens with the per-call
/// resolver that can map those tokens back to secure retrievers.
/// </summary>
public sealed record MaterializedAgentDefinition(
    AgentDefinition Definition,
    ISecretPlaceholderResolver Resolver);
