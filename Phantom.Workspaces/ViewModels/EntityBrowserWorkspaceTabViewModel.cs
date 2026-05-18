using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly SubscribedGet subscribedGet;

    public EntityBrowserWorkspaceTabViewModel(
        SubscribedGet subscribedGet)
    {
        this.subscribedGet = subscribedGet;
        this.subscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
        this.RebuildTree();
    }

    public ObservableCollection<EntityBrowserTreeNodeViewModel> RootEntities { get; } = new();

    private void OnSubscribedResultsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.RebuildTree();
    }

    private void RebuildTree()
    {
        var nodes = new List<EntityBrowserTreeNodeViewModel>();
        foreach (var entity in this.subscribedGet.Results)
        {
            if (entity.Snapshot.Data is not JsonElement entityData)
            {
                continue;
            }

            if (!EntityBrowserTreeNodeViewModel.TryGetPrimaryName(entityData, out var name))
            {
                continue;
            }

            var sortKey = string.Join("/", name.Components);
            nodes.Add(new EntityBrowserTreeNodeViewModel(entity, name.Components, sortKey));
        }

        var nodeByPath = new Dictionary<string, EntityBrowserTreeNodeViewModel>(StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(static node => node.SortKey, StringComparer.Ordinal))
        {
            if (node.NameComponents.Count == 0)
            {
                continue;
            }

            var pathKey = string.Join("/", node.NameComponents);
            nodeByPath.TryAdd(pathKey, node);
        }

        var childrenByParent = new Dictionary<string, List<EntityBrowserTreeNodeViewModel>>(StringComparer.Ordinal);
        var rootNodes = new List<EntityBrowserTreeNodeViewModel>();
        foreach (var node in nodes)
        {
            if (node.NameComponents.Count <= 1)
            {
                rootNodes.Add(node);
                continue;
            }

            var parentPath = string.Join("/", node.NameComponents.Take(node.NameComponents.Count - 1));
            if (!nodeByPath.ContainsKey(parentPath))
            {
                rootNodes.Add(node);
                continue;
            }

            if (!childrenByParent.TryGetValue(parentPath, out var parentChildren))
            {
                parentChildren = new List<EntityBrowserTreeNodeViewModel>();
                childrenByParent[parentPath] = parentChildren;
            }

            parentChildren.Add(node);
        }

        foreach (var pair in nodeByPath)
        {
            if (!childrenByParent.TryGetValue(pair.Key, out var children))
            {
                pair.Value.SetChildren(Array.Empty<EntityBrowserTreeNodeViewModel>());
                continue;
            }

            pair.Value.SetChildren(children.OrderBy(static child => child.SortKey, StringComparer.Ordinal).ToArray());
        }

        this.RootEntities.Clear();
        foreach (var rootNode in rootNodes.OrderBy(static node => node.SortKey, StringComparer.Ordinal))
        {
            this.RootEntities.Add(rootNode);
        }
    }
}
