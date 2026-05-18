using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly EntityBroker entityBroker;
    private readonly SubscribedGet rootSubscribedGet;
    private readonly EntityListViewModel entityList = new();
    private readonly Dictionary<string, SubscribedGet> subscribedGetsByPath = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSubscriptions = new(StringComparer.Ordinal);
    private string? stickyFocusItemKey;

    public EntityBrowserWorkspaceTabViewModel(
        EntityBroker entityBroker,
        SubscribedGet subscribedGet)
    {
        this.entityBroker = entityBroker;
        this.rootSubscribedGet = subscribedGet;
        this.rootSubscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
        this.RebuildTree();
    }

    public EntityListViewModel EntityList => this.entityList;

    public ObservableCollection<EntityHierarchyContextItemViewModel> StickyParentItems { get; } = [];

    public bool HasStickyParentItems => this.StickyParentItems.Count > 0;

    public void UpdateStickyContextFromVisibleItem(
        string? itemKey)
    {
        if (string.Equals(this.stickyFocusItemKey, itemKey, StringComparison.Ordinal))
        {
            return;
        }

        this.stickyFocusItemKey = itemKey;
        this.RebuildStickyParentItems();
    }

    private void OnSubscribedResultsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.RebuildTree();
    }

    private void RebuildTree()
    {
        var expansionStateByPath = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in this.entityList.Items)
        {
            expansionStateByPath[item.ItemKey] = item.IsExpanded;
        }

        var rootChildren = this.BuildChildren(Array.Empty<string>(), this.rootSubscribedGet.Results, expansionStateByPath);
        var items = this.BuildItems(rootChildren, expansionStateByPath);
        this.entityList.SetItems(items);
        if (this.stickyFocusItemKey is null)
        {
            this.stickyFocusItemKey = this.entityList.Items.FirstOrDefault()?.ItemKey;
        }

        this.RebuildStickyParentItems();
    }

    private IReadOnlyCollection<EntityListNodeViewModel> BuildChildren(
        IReadOnlyCollection<string> parentPath,
        IReadOnlyCollection<SubscribedEntityViewModel> entities,
        IReadOnlyDictionary<string, bool> expansionStateByPath)
    {
        var children = new Dictionary<string, EntityListNodeViewModel>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (entity.Snapshot.Data is not JsonElement entityData
                || !entityData.TryGetProperty("names", out var namesElement)
                || namesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var nameElement in namesElement.EnumerateArray())
            {
                var parsedName = nameElement.TryReadEntityName();
                if (parsedName is null)
                {
                    continue;
                }

                var nameComponents = parsedName.Value.Components;
                if (nameComponents.Length != parentPath.Count + 1
                    || !nameComponents.Take(parentPath.Count).SequenceEqual(parentPath, StringComparer.Ordinal))
                {
                    continue;
                }

                var sortKey = JsonSerializer.Serialize(nameComponents);
                if (children.ContainsKey(sortKey))
                {
                    continue;
                }

                var node = new EntityListNodeViewModel(entity, nameComponents, sortKey);
                children.Add(sortKey, node);
            }
        }

        foreach (var pair in children)
        {
            var pathKey = pair.Key;
            var node = pair.Value;
            this.EnsureChildSubscription(node.NameComponents, pathKey);

            if (!this.subscribedGetsByPath.TryGetValue(pathKey, out var childGet))
            {
                node.SetChildren(Array.Empty<EntityListNodeViewModel>());
                continue;
            }

            node.SetChildren(this.BuildChildren(node.NameComponents, childGet.Results, expansionStateByPath));
        }

        return children
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .ToArray();
    }

    private IReadOnlyCollection<EntityListItemViewModel> BuildItems(
        IReadOnlyCollection<EntityListNodeViewModel> rootChildren,
        IReadOnlyDictionary<string, bool> expansionStateByPath)
    {
        var items = new List<EntityListItemViewModel>();
        var order = 0;
        foreach (var rootNode in rootChildren.OrderBy(static node => node.SortKey, StringComparer.Ordinal))
        {
            this.AddItemsDepthFirst(
                rootNode,
                parentItemKey: null,
                level: 0,
                parentVisible: true,
                expansionStateByPath,
                items,
                ref order);
        }

        return items;
    }

    private void AddItemsDepthFirst(
        EntityListNodeViewModel node,
        string? parentItemKey,
        int level,
        bool parentVisible,
        IReadOnlyDictionary<string, bool> expansionStateByPath,
        ICollection<EntityListItemViewModel> items,
        ref int order)
    {
        var itemKey = JsonSerializer.Serialize(node.NameComponents);
        var childItemKeys = node.Children
            .OrderBy(static child => child.SortKey, StringComparer.Ordinal)
            .Select(static child => JsonSerializer.Serialize(child.NameComponents))
            .ToArray();
        var isExpanded = expansionStateByPath.TryGetValue(itemKey, out var expanded) && expanded;
        node.IsExpanded = isExpanded;

        if (parentVisible)
        {
            var item = new EntityListItemViewModel(
                node,
                order: order++,
                level: level,
                itemKey: itemKey,
                parentItemKey: parentItemKey,
                childItemKeys: childItemKeys,
                isExpanded: isExpanded);
            item.PropertyChanged += this.OnItemPropertyChanged;
            items.Add(item);
        }

        var childVisible = parentVisible && isExpanded;
        foreach (var child in node.Children.OrderBy(static child => child.SortKey, StringComparer.Ordinal))
        {
            this.AddItemsDepthFirst(
                child,
                itemKey,
                level + 1,
                childVisible,
                expansionStateByPath,
                items,
                ref order);
        }
    }

    private void OnItemPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(EntityListItemViewModel.IsExpanded), StringComparison.Ordinal))
        {
            return;
        }

        this.RebuildTree();
    }

    private void RebuildStickyParentItems()
    {
        this.StickyParentItems.Clear();

        var rootLevel = 0;
        this.StickyParentItems.Add(new EntityHierarchyContextItemViewModel("root", "folder", rootLevel));
        if (string.IsNullOrWhiteSpace(this.stickyFocusItemKey))
        {
            this.RaisePropertyChanged(nameof(this.HasStickyParentItems));
            return;
        }

        var itemsByKey = this.entityList.Items.ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
        if (!itemsByKey.TryGetValue(this.stickyFocusItemKey, out var focusedItem))
        {
            this.RaisePropertyChanged(nameof(this.HasStickyParentItems));
            return;
        }

        var ancestorStack = new Stack<EntityListItemViewModel>();
        var parentKey = focusedItem.ParentItemKey;
        while (parentKey is not null && itemsByKey.TryGetValue(parentKey, out var ancestor))
        {
            ancestorStack.Push(ancestor);
            parentKey = ancestor.ParentItemKey;
        }

        var level = 1;
        while (ancestorStack.Count > 0)
        {
            var ancestor = ancestorStack.Pop();
            this.StickyParentItems.Add(new EntityHierarchyContextItemViewModel(ancestor.DisplayName, ancestor.EntityType, level));
            level++;
        }

        this.RaisePropertyChanged(nameof(this.HasStickyParentItems));
    }

    private void EnsureChildSubscription(
        IReadOnlyCollection<string> childPath,
        string pathKey)
    {
        if (this.subscribedGetsByPath.ContainsKey(pathKey)
            || this.pendingSubscriptions.Contains(pathKey))
        {
            return;
        }

        this.pendingSubscriptions.Add(pathKey);
        _ = this.SubscribeChildPathAsync(childPath.ToArray(), pathKey);
    }

    private async Task SubscribeChildPathAsync(
        IReadOnlyCollection<string> childPath,
        string pathKey)
    {
        try
        {
            var subscribedGet = await this.entityBroker.SubscribeGetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityName = new EntityName(childPath.ToArray()),
                            EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                        },
                    ],
                    Timestamps = [null],
                });
            subscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
            this.subscribedGetsByPath[pathKey] = subscribedGet;
            this.RebuildTree();
        }
        finally
        {
            this.pendingSubscriptions.Remove(pathKey);
        }
    }
}
