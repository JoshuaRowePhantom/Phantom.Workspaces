using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IToolResourceFactory"/> that composes several child factories,
/// trying each in order until one resolves the supplied tool resource.
/// </summary>
public sealed class ComposingToolResourceFactory : IToolResourceFactory
{
    private readonly IReadOnlyList<IToolResourceFactory> factories;

    public ComposingToolResourceFactory(params IToolResourceFactory[] factories)
        : this((IReadOnlyList<IToolResourceFactory>)factories)
    {
    }

    public ComposingToolResourceFactory(IReadOnlyList<IToolResourceFactory> factories)
    {
        this.factories = factories;
    }

    public async Task<Tool?> ResolveToolResourceAsync(
        ToolResource toolResource,
        CancellationToken cancellationToken = default)
    {
        foreach (var factory in this.factories)
        {
            var tool = await factory.ResolveToolResourceAsync(toolResource, cancellationToken).ConfigureAwait(false);
            if (tool is not null)
            {
                return tool;
            }
        }

        return null;
    }
}
