using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly SubscribedGet subscribedGet;
    private readonly EntityListViewModel entityList = new();

    public EntityBrowserWorkspaceTabViewModel(
        SubscribedGet subscribedGet)
    {
        this.subscribedGet = subscribedGet;
        this.subscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
        this.RebuildTree();
    }

    public ObservableCollection<EntityListNodeViewModel> RootEntities => this.entityList.RootEntities;

    private void OnSubscribedResultsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.RebuildTree();
    }

    private void RebuildTree()
    {
        this.entityList.PopulateFromEntities(this.subscribedGet.Results, includeRootNode: true);
    }
}
