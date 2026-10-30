using System.Collections.Generic;
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

    /// <summary>
    /// The original <c>${SECRET:Name}</c> reference strings whose acquisition was deliberately
    /// deferred from this preflight materialization to the owning MCP server's connection boundary
    /// (issue #1672). These references remain unrewritten in <see cref="Definition"/>.
    /// </summary>
    public IReadOnlyCollection<string> DeferredSecretReferences { get; init; } = [];

    public ValueTask DisposeAsync() => this.Resolver.DisposeAsync();
}
