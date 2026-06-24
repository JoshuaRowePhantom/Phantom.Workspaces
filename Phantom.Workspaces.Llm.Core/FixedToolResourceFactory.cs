using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves tool resources into concrete tools using a mapping supplied at construction time.
/// The factory encodes no built-in behavior of its own: a tool resource resolves only when its
/// (id, name) pair is present in the supplied mapping.
/// </summary>
public sealed class FixedToolResourceFactory : IToolResourceFactory
{
    private readonly IReadOnlyDictionary<(string Id, string Name), Tool> toolsByResource;

    /// <summary>
    /// Creates a factory that resolves tool resources from the supplied mapping, keyed by the
    /// tool resource's (id, name) pair.
    /// </summary>
    public FixedToolResourceFactory(IReadOnlyDictionary<(string Id, string Name), Tool> toolsByResource)
    {
        this.toolsByResource = toolsByResource;
    }

    public Task<Tool?> ResolveToolResourceAsync(
        ToolResource toolResource,
        CancellationToken cancellationToken = default)
    {
        if (toolResource.Id is null || toolResource.Name is null)
        {
            return Task.FromResult<Tool?>(null);
        }

        return Task.FromResult(
            this.toolsByResource.TryGetValue((toolResource.Id, toolResource.Name), out var tool)
                ? tool
                : null);
    }
}
