using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly EntityBroker entityBroker;
    private readonly ISchemaAccessor schemaAccessor;
    private readonly FieldTypeResolver fieldTypeResolver;
    private readonly SubscribedGet rootSubscribedGet;
    private readonly EntityListViewModel entityList = new();
    private readonly Dictionary<string, SubscribedGet> subscribedGetsByPath = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSubscriptions = new(StringComparer.Ordinal);

    public EntityBrowserWorkspaceTabViewModel(
        EntityBroker entityBroker,
        SubscribedGet subscribedGet)
    {
        this.entityBroker = entityBroker;
        this.schemaAccessor = new SchemaAccessor(this.entityBroker.EntityRepository.DataAccessLayer);
        this.fieldTypeResolver = new FieldTypeResolver(this.schemaAccessor);
        this.rootSubscribedGet = subscribedGet;
        this.rootSubscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
        _ = this.RebuildTreeAsync();
    }

    public EntityListViewModel EntityList => this.entityList;

    private void OnSubscribedResultsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        _ = this.RebuildTreeAsync();
    }

    private async Task RebuildTreeAsync()
    {
        var expansionStateByPath = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in this.entityList.Items)
        {
            expansionStateByPath[item.ItemKey] = item.IsExpanded;
        }

        var rootChildren = await this.BuildChildrenAsync(Array.Empty<string>(), this.rootSubscribedGet.Results, expansionStateByPath);
        var items = this.BuildItems(this.rootSubscribedGet.Results, rootChildren, expansionStateByPath);
        this.entityList.SetItems(items);
    }

    private async Task<IReadOnlyCollection<EntityListNodeViewModel>> BuildChildrenAsync(
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

                var node = new EntityListNodeViewModel(
                    entity,
                    nameComponents,
                    sortKey,
                    await this.BuildFieldEditorsAsync(entity));
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

            node.SetChildren(await this.BuildChildrenAsync(node.NameComponents, childGet.Results, expansionStateByPath));
        }

        return children
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .ToArray();
    }

    private IReadOnlyCollection<EntityListItemViewModel> BuildItems(
        IReadOnlyCollection<SubscribedEntityViewModel> rootEntities,
        IReadOnlyCollection<EntityListNodeViewModel> rootChildren,
        IReadOnlyDictionary<string, bool> expansionStateByPath)
    {
        var items = new List<EntityListItemViewModel>();
        var order = 0;
        if (this.TryFindEntityForPath(rootEntities, Array.Empty<string>(), out var rootEntity))
        {
            var rootNode = new EntityListNodeViewModel(rootEntity, Array.Empty<string>(), "[]");
            rootNode.SetChildren(rootChildren);
            this.AddItemsDepthFirst(
                rootNode,
                parentItemKey: null,
                level: 0,
                parentVisible: true,
                expansionStateByPath,
                items,
                ref order);
            return items;
        }

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
        var isExpanded = expansionStateByPath.TryGetValue(itemKey, out var expanded)
            ? expanded
            : node.NameComponents.Count == 0;
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

        _ = this.RebuildTreeAsync();
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
            await this.RebuildTreeAsync();
        }
        finally
        {
            this.pendingSubscriptions.Remove(pathKey);
        }
    }

    private bool TryFindEntityForPath(
        IReadOnlyCollection<SubscribedEntityViewModel> entities,
        IReadOnlyCollection<string> path,
        out SubscribedEntityViewModel entityForPath)
    {
        foreach (var entity in entities)
        {
            if (entity.Snapshot.Data is not JsonElement data
                || !data.TryGetProperty("names", out var names)
                || names.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var nameElement in names.EnumerateArray())
            {
                if (nameElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var components = nameElement.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                if (components.Length == path.Count
                    && components.SequenceEqual(path, StringComparer.Ordinal))
                {
                    entityForPath = entity;
                    return true;
                }
            }
        }

        entityForPath = null!;
        return false;
    }

    private async Task<IReadOnlyCollection<EntityFieldEditorViewModel>> BuildFieldEditorsAsync(
        SubscribedEntityViewModel entity)
    {
        if (entity.Data is not JsonElement entityData || entityData.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<EntityFieldEditorViewModel>();
        }

        var fieldNames = await this.fieldTypeResolver.EnumerateObjectFieldNamesAsync(
            entityData,
            Array.Empty<string>(),
            entityData);

        var editors = new List<EntityFieldEditorViewModel>();
        foreach (var fieldName in fieldNames)
        {
            if (!entityData.TryGetProperty(fieldName, out var fieldValue))
            {
                using var nullDocument = JsonDocument.Parse("null");
                fieldValue = nullDocument.RootElement.Clone();
            }

            editors.Add(await this.CreateFieldEditorAsync(entityData, fieldName, fieldValue, [fieldName]));
        }

        return editors;
    }

    private async Task<EntityFieldEditorViewModel> CreateFieldEditorAsync(
        JsonElement rootEntity,
        string fieldName,
        JsonElement fieldValue,
        IReadOnlyList<string> fieldPath)
    {
        var resolvedType = await this.fieldTypeResolver.ResolveFieldTypeAsync(rootEntity, fieldPath, fieldValue);
        switch (resolvedType.TypeName)
        {
            case "local-string":
                if (fieldValue.ValueKind == JsonValueKind.Object)
                {
                    var localizedValues = fieldValue.EnumerateObject()
                        .Where(static property => property.Value.ValueKind == JsonValueKind.String)
                        .Select(property => new StringFieldEditorViewModel(property.Name, property.Value.GetString() ?? string.Empty))
                        .ToArray();
                    return new LocalStringFieldEditorViewModel(fieldName, localizedValues);
                }

                return new LocalStringFieldEditorViewModel(
                    fieldName,
                    [new StringFieldEditorViewModel("default", fieldValue.ValueKind == JsonValueKind.String ? fieldValue.GetString() ?? string.Empty : string.Empty)]);
            case "mime-attachment":
                if (fieldValue.ValueKind == JsonValueKind.Object)
                {
                    var mimeType = fieldValue.TryGetProperty("mime-type", out var mimeTypeElement)
                        && mimeTypeElement.ValueKind == JsonValueKind.String
                        ? mimeTypeElement.GetString()!
                        : resolvedType.DefaultMimeType ?? "application/octet-stream";
                    var textContent = fieldValue.TryGetProperty("content", out var contentElement)
                                      && contentElement.ValueKind == JsonValueKind.Object
                                      && contentElement.TryGetProperty("text", out var textElement)
                                      && textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString()
                        : null;
                    var url = fieldValue.TryGetProperty("url", out var urlElement)
                              && urlElement.ValueKind == JsonValueKind.String
                        ? urlElement.GetString()
                        : null;
                    return new MimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url);
                }

                return new MimeAttachmentFieldEditorViewModel(
                    fieldName,
                    resolvedType.DefaultMimeType ?? "application/octet-stream",
                    null,
                    null);
            case "array":
                if (fieldValue.ValueKind != JsonValueKind.Array)
                {
                    return new ArrayFieldEditorViewModel(fieldName, Array.Empty<EntityFieldEditorViewModel>());
                }

                var items = new List<EntityFieldEditorViewModel>();
                var itemIndex = 0;
                foreach (var item in fieldValue.EnumerateArray())
                {
                    var itemPath = fieldPath.Concat([itemIndex.ToString()]).ToArray();
                    items.Add(await this.CreateFieldEditorAsync(rootEntity, $"[{itemIndex}]", item, itemPath));
                    itemIndex++;
                }

                return new ArrayFieldEditorViewModel(fieldName, items);
            case "object":
                if (fieldValue.ValueKind != JsonValueKind.Object)
                {
                    return new ObjectFieldEditorViewModel(fieldName, Array.Empty<EntityFieldEditorViewModel>());
                }

                var childFieldNames = await this.fieldTypeResolver.EnumerateObjectFieldNamesAsync(
                    rootEntity,
                    fieldPath,
                    fieldValue);
                var childEditors = new List<EntityFieldEditorViewModel>();
                foreach (var childFieldName in childFieldNames)
                {
                    if (!fieldValue.TryGetProperty(childFieldName, out var childValue))
                    {
                        using var nullDocument = JsonDocument.Parse("null");
                        childValue = nullDocument.RootElement.Clone();
                    }

                    childEditors.Add(
                        await this.CreateFieldEditorAsync(
                            rootEntity,
                            childFieldName,
                            childValue,
                            fieldPath.Concat([childFieldName]).ToArray()));
                }

                return new ObjectFieldEditorViewModel(fieldName, childEditors);
            default:
                return new StringFieldEditorViewModel(fieldName, fieldValue.ToString());
        }
    }
}
