using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves fixed tool resources (id == <see cref="FixedToolResources.FixedToolResourceId"/>)
/// into <see cref="CustomTool"/> instances whose <see cref="Tool.Kind"/> matches the resource
/// name. These tools are then handled by the built-in toolset factories.
/// </summary>
public sealed class FixedToolResourceFactory : IToolResourceFactory
{
    private readonly IReadOnlyCollection<string> supportedNames;

    public FixedToolResourceFactory()
        : this(FixedToolResources.DefaultNames)
    {
    }

    public FixedToolResourceFactory(IReadOnlyCollection<string> supportedNames)
    {
        this.supportedNames = supportedNames;
    }

    public Task<Tool?> ResolveToolResourceAsync(
        ToolResource toolResource,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(toolResource.Id, FixedToolResources.FixedToolResourceId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(toolResource.Name)
            || !this.supportedNames.Contains(toolResource.Name))
        {
            return Task.FromResult<Tool?>(null);
        }

        var tool = new CustomTool
        {
            Kind = toolResource.Name,
            Name = toolResource.Name,
            Options = toolResource.Options,
        };

        return Task.FromResult<Tool?>(tool);
    }
}
