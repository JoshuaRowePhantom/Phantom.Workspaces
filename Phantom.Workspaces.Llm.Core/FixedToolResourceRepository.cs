using System.Collections.ObjectModel;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IToolResourceRepository"/> that exposes a fixed set of tool resources supplied at
/// construction time. The repository encodes no built-in tool resources of its own.
/// </summary>
public sealed class FixedToolResourceRepository : IToolResourceRepository
{
    /// <summary>
    /// Creates a repository exposing the supplied tool resources.
    /// </summary>
    public FixedToolResourceRepository(IReadOnlyList<ToolResource> toolResources)
    {
        var collection = new ObservableCollection<ToolResource>(toolResources);
        this.ToolResources = new ReadOnlyObservableCollection<ToolResource>(collection);
    }

    public ReadOnlyObservableCollection<ToolResource> ToolResources { get; }
}
