using AgentSchema;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Bundles an agent definition rewritten to opaque secret-reference tokens with the per-call
/// resolver that owns the corresponding fixed-generation leases.
/// </summary>
public sealed record MaterializedAgentDefinition : IAsyncDisposable
{
    public required AgentDefinition Definition { get; init; }
    public required ISecretPlaceholderResolver Resolver { get; init; }

    public ValueTask DisposeAsync() => this.Resolver.DisposeAsync();
}
