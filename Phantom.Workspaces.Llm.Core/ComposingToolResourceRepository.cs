using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IToolResourceRepository"/> that composes several child repositories,
/// exposing the union of their tool resources. The aggregate collection updates reactively
/// as child repositories add or remove tool resources.
/// </summary>
public sealed class ComposingToolResourceRepository : IToolResourceRepository
{
    private readonly IReadOnlyList<IToolResourceRepository> repositories;
    private readonly ObservableCollection<ToolResource> aggregate = [];

    public ComposingToolResourceRepository(params IToolResourceRepository[] repositories)
        : this((IReadOnlyList<IToolResourceRepository>)repositories)
    {
    }

    public ComposingToolResourceRepository(IReadOnlyList<IToolResourceRepository> repositories)
    {
        this.repositories = repositories;
        this.ToolResources = new ReadOnlyObservableCollection<ToolResource>(this.aggregate);

        foreach (var repository in this.repositories)
        {
            ((INotifyCollectionChanged)repository.ToolResources).CollectionChanged += this.OnChildCollectionChanged;
        }

        this.Rebuild();
    }

    public ReadOnlyObservableCollection<ToolResource> ToolResources { get; }

    private void OnChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.Rebuild();
    }

    private void Rebuild()
    {
        this.aggregate.Clear();
        foreach (var repository in this.repositories)
        {
            foreach (var toolResource in repository.ToolResources)
            {
                this.aggregate.Add(toolResource);
            }
        }
    }
}
