using System.Collections.ObjectModel;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IToolResourceRepository"/> that exposes a fixed set of tool resources
/// for the built-in toolsets (workspace entity, filesystem, web, etc.). These resolve to
/// concrete tools via <see cref="FixedToolResourceFactory"/>.
/// </summary>
public sealed class FixedToolResourceRepository : IToolResourceRepository
{
    public FixedToolResourceRepository()
        : this(FixedToolResources.DefaultNames)
    {
    }

    public FixedToolResourceRepository(IReadOnlyList<string> names)
    {
        var collection = new ObservableCollection<ToolResource>(
            names.Select(static name => new ToolResource
            {
                Kind = "tool",
                Id = FixedToolResources.FixedToolResourceId,
                Name = name,
            }));
        this.ToolResources = new ReadOnlyObservableCollection<ToolResource>(collection);
    }

    public ReadOnlyObservableCollection<ToolResource> ToolResources { get; }
}
