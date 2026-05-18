using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListViewModel : ViewModelBase
{
    public ObservableCollection<EntityListNodeViewModel> RootEntities { get; } = new();

    public void PopulateFromEntities(
        IReadOnlyCollection<SubscribedEntityViewModel> entities,
        bool includeRootNode)
    {
        var nodes = new List<EntityListNodeViewModel>();
        foreach (var entity in entities)
        {
            if (entity.Snapshot.Data is not JsonElement entityData)
            {
                continue;
            }

            if (!EntityListNodeViewModel.TryGetPrimaryName(entityData, out var name))
            {
                continue;
            }

            nodes.Add(
                new EntityListNodeViewModel(
                    entity,
                    name.Components,
                    sortKey: JsonSerializer.Serialize(name.Components)));
        }

        var nodeByName = nodes
            .Where(static node => node.NameComponents.Count > 0)
            .ToDictionary(
                static node => new EntityName(node.NameComponents.ToArray()),
                static node => node);

        var childrenByParent = new Dictionary<EntityName, List<EntityListNodeViewModel>>();
        var rootNodes = new List<EntityListNodeViewModel>();
        foreach (var node in nodes.OrderBy(static node => node.SortKey, StringComparer.Ordinal))
        {
            if (node.NameComponents.Count <= 1)
            {
                rootNodes.Add(node);
                continue;
            }

            var parentName = new EntityName(node.NameComponents.Take(node.NameComponents.Count - 1).ToArray());
            if (!nodeByName.ContainsKey(parentName))
            {
                rootNodes.Add(node);
                continue;
            }

            if (!childrenByParent.TryGetValue(parentName, out var parentChildren))
            {
                parentChildren = new List<EntityListNodeViewModel>();
                childrenByParent[parentName] = parentChildren;
            }

            parentChildren.Add(node);
        }

        foreach (var pair in nodeByName)
        {
            if (!childrenByParent.TryGetValue(pair.Key, out var children))
            {
                pair.Value.SetChildren(Array.Empty<EntityListNodeViewModel>());
                continue;
            }

            pair.Value.SetChildren(children.OrderBy(static child => child.SortKey, StringComparer.Ordinal).ToArray());
        }

        this.RootEntities.Clear();
        if (includeRootNode)
        {
            var root = new EntityListNodeViewModel(
                displayName: "Root",
                entityType: "folder",
                nameComponents: Array.Empty<string>(),
                sortKey: string.Empty,
                isExpanded: true);
            root.SetChildren(rootNodes.OrderBy(static node => node.SortKey, StringComparer.Ordinal).ToArray());
            this.RootEntities.Add(root);
            return;
        }

        foreach (var rootNode in rootNodes.OrderBy(static node => node.SortKey, StringComparer.Ordinal))
        {
            this.RootEntities.Add(rootNode);
        }
    }
}
