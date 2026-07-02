using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly EntityCardViewResolver entityCardViewResolver = new();
    private readonly EntityBroker entityBroker;
    private readonly ISchemaAccessor schemaAccessor;
    private readonly FieldTypeResolver fieldTypeResolver;
    private readonly EntityReferenceSearch entityReferenceSearch;
    private readonly SubscribedGet rootSubscribedGet;
    private readonly EntityListViewModel entityList = new();
    private readonly Dictionary<string, SubscribedGet> subscribedGetsByPath = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSubscriptions = new(StringComparer.Ordinal);
    private bool isRebuilding;
    private bool isRebuildPending;

    public EntityBrowserWorkspaceTabViewModel(
        EntityBroker entityBroker,
        SubscribedGet subscribedGet)
    {
        this.entityBroker = entityBroker;
        this.schemaAccessor = new SchemaAccessor(this.entityBroker.EntityRepository.DataAccessLayer);
        this.fieldTypeResolver = new FieldTypeResolver(this.schemaAccessor);
        this.entityReferenceSearch = new EntityReferenceSearch(this.entityBroker);
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
        if (this.isRebuilding)
        {
            this.isRebuildPending = true;
            return;
        }

        this.isRebuilding = true;
        try
        {
            do
            {
                this.isRebuildPending = false;

                var expansionStateByPath = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var item in this.entityList.Items)
                {
                    expansionStateByPath[item.ItemKey] = item.IsExpanded;
                }

                var rootChildren = await this.BuildChildrenAsync(Array.Empty<string>(), this.rootSubscribedGet.Results, expansionStateByPath);
                var items = this.BuildItems(this.rootSubscribedGet.Results, rootChildren, expansionStateByPath);
                this.entityList.SetItems(items);
            }
            while (this.isRebuildPending);
        }
        finally
        {
            this.isRebuilding = false;
            this.isRebuildPending = false;
        }
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
                    await this.BuildFieldEditorsAsync(entity),
                    cardViewName: this.entityCardViewResolver.ResolveViewName(entity, EntityCardViewResolver.RawViewName));
                children.Add(sortKey, node);
            }
        }

        foreach (var pair in children)
        {
            var pathKey = pair.Key;
            var node = pair.Value;
            
            // Set the expansion callback so we can manage subscriptions during user interaction
            node.SetExpansionChangedCallback(this.OnNodeExpansionChanged);
            
            // Always subscribe to this node's children so we can determine HasChildren
            this.EnsureChildSubscription(node.NameComponents, pathKey);
            
            // If subscription results are available, populate children based on expansion state
            if (this.subscribedGetsByPath.TryGetValue(pathKey, out var childGet))
            {
                // Check if this node should be expanded
                var isExpanded = expansionStateByPath.TryGetValue(pathKey, out var expanded)
                    ? expanded
                    : node.NameComponents.Count == 0; // Root is expanded by default
                
                if (isExpanded)
                {
                    // Node is expanded, recursively build all descendants
                    node.SetChildren(await this.BuildChildrenAsync(node.NameComponents, childGet.Results, expansionStateByPath));
                }
                else
                {
                    // Node is collapsed, build immediate children as simple leaf nodes (no recursion)
                    node.SetChildren(this.BuildChildrenNonRecursive(node.NameComponents, childGet.Results));
                }
            }
            // If subscription isn't ready yet, leave children empty
            // The subscription's Results.CollectionChanged event will trigger a rebuild when ready
            else
            {
                node.SetChildren(Array.Empty<EntityListNodeViewModel>());
            }
        }

        return children
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .ToArray();
    }

    private IReadOnlyCollection<EntityListNodeViewModel> BuildChildrenNonRecursive(
        IReadOnlyCollection<string> parentPath,
        IReadOnlyCollection<SubscribedEntityViewModel> entities)
    {
        // Build immediate children as leaf nodes without any recursion or field-editor resolution.
        // Field editors are intentionally omitted: these children are inside a collapsed folder and
        // are not visible, so resolving their field types (which hits the thread pool and schema DAL)
        // would be wasted work. Field editors are built in BuildChildrenAsync when the node is expanded.
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
                    cardViewName: this.entityCardViewResolver.ResolveViewName(entity, EntityCardViewResolver.RawViewName));
                
                node.SetExpansionChangedCallback(this.OnNodeExpansionChanged);
                this.EnsureChildSubscription(nameComponents, sortKey);
                node.SetChildren(Array.Empty<EntityListNodeViewModel>());
                
                children.Add(sortKey, node);
            }
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
            var rootNode = new EntityListNodeViewModel(
                rootEntity,
                Array.Empty<string>(),
                "[]",
                cardViewName: this.entityCardViewResolver.ResolveViewName(rootEntity, EntityCardViewResolver.RawViewName));
            rootNode.SetExpansionChangedCallback(this.OnNodeExpansionChanged);
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
            _ = this.RebuildTreeAsync();
        }
        finally
        {
            this.pendingSubscriptions.Remove(pathKey);
        }
    }

    private void OnNodeExpansionChanged(
        EntityListNodeViewModel node,
        bool isExpanded)
    {
        // Don't trigger rebuild during tree rebuild — mark pending so the current rebuild
        // loop runs another iteration and picks up the new expansion state.
        if (this.isRebuilding)
        {
            this.isRebuildPending = true;
            return;
        }
        
        if (!isExpanded)
        {
            // Node collapsed: dispose subscriptions for all descendants to save resources
            this.DisposeDescendantSubscriptions(node);
        }
        
        // Expansion state changed - rebuild tree to reflect new state
        _ = this.RebuildTreeAsync();
    }

    private void DisposeDescendantSubscriptions(EntityListNodeViewModel node)
    {
        // Recursively dispose subscriptions for all descendants
        foreach (var child in node.Children)
        {
            var childKey = JsonSerializer.Serialize(child.NameComponents);
            this.DisposeChildSubscription(childKey);
            this.DisposeDescendantSubscriptions(child);
        }
    }

    private void DisposeChildSubscription(string pathKey)
    {
        if (!this.subscribedGetsByPath.TryGetValue(pathKey, out var subscribedGet))
        {
            return;
        }

        subscribedGet.Results.CollectionChanged -= this.OnSubscribedResultsChanged;
        this.subscribedGetsByPath.Remove(pathKey);
        // The SubscribedGet will be garbage collected and EntityBroker will clean up the WeakReference
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
        var resolvedType = await Task.Run(() => this.fieldTypeResolver.ResolveFieldTypeAsync(rootEntity, fieldPath, fieldValue));

        // Entity-reference editor when the field's schema declares allowed entity types, so the browser
        // renders related entities (for example a relationship's participants) as their display names.
        if (resolvedType.EntityTypes.Count > 0)
        {
            var referencedId = fieldValue.ValueKind == JsonValueKind.String ? fieldValue.GetString() : null;
            var referenceEditor = new EntityReferenceFieldEditorViewModel(
                fieldName,
                referencedId,
                resolvedType.EntityTypes,
                this.entityReferenceSearch);
            await referenceEditor.ResolveCurrentValueAsync();
            return referenceEditor;
        }

        switch (resolvedType.TypeName)
        {
            case "local-string":
                if (fieldValue.ValueKind == JsonValueKind.String)
                {
                    return new LocalStringFieldEditorViewModel(fieldName, fieldValue.GetString() ?? string.Empty);
                }

                if (fieldValue.ValueKind == JsonValueKind.Object)
                {
                    var localizedValues = fieldValue.EnumerateObject()
                        .Where(static property => property.Value.ValueKind == JsonValueKind.String)
                        .Select(property => new LocalizedTextValueViewModel(property.Name, property.Value.GetString() ?? string.Empty))
                        .ToArray();
                    if (localizedValues.Length > 0)
                    {
                        return new LocalStringFieldEditorViewModel(fieldName, localizedValues);
                    }
                }

                return new LocalStringFieldEditorViewModel(fieldName, fieldValue.ToString());
            case "mime-attachment":
                if (fieldValue.ValueKind == JsonValueKind.Object)
                {
                    if (TryCreateLocalizedMimeAttachmentEditor(fieldName, fieldValue, resolvedType.DefaultMimeType, out var localizedMimeEditor))
                    {
                        return localizedMimeEditor;
                    }

                    var mimeAttachmentValue = fieldValue;
                    if (!fieldValue.TryGetProperty("mime-type", out _)
                        && fieldValue.TryGetProperty("default", out var defaultMimeAttachment)
                        && defaultMimeAttachment.ValueKind == JsonValueKind.Object)
                    {
                        mimeAttachmentValue = defaultMimeAttachment;
                    }

                    var mimeType = mimeAttachmentValue.TryGetProperty("mime-type", out var mimeTypeElement)
                        && mimeTypeElement.ValueKind == JsonValueKind.String
                        ? mimeTypeElement.GetString()!
                        : resolvedType.DefaultMimeType ?? "application/octet-stream";
                    var textContent = mimeAttachmentValue.TryGetProperty("content", out var contentElement)
                                      && contentElement.ValueKind == JsonValueKind.Object
                                      && contentElement.TryGetProperty("text", out var textElement)
                                      && textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString()
                        : null;
                    var url = mimeAttachmentValue.TryGetProperty("url", out var urlElement)
                              && urlElement.ValueKind == JsonValueKind.String
                        ? urlElement.GetString()
                        : null;
                    MimeAttachmentFieldEditorViewModel editor = string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
                        ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url)
                        : new PlainMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url);
                    return new LocalizedMimeAttachmentFieldEditorViewModel(fieldName, editor);
                }

                var defaultMimeType = resolvedType.DefaultMimeType ?? "application/octet-stream";
                MimeAttachmentFieldEditorViewModel defaultEditor = string.Equals(defaultMimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
                    ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, defaultMimeType, null, null)
                    : new PlainMimeAttachmentFieldEditorViewModel(fieldName, defaultMimeType, null, null);
                return new LocalizedMimeAttachmentFieldEditorViewModel(fieldName, defaultEditor);
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

                if (TryCreateJsonSchemaEditor(fieldName, fieldValue, out var jsonSchemaEditor))
                {
                    return jsonSchemaEditor;
                }

                if (TryCreateMimeAttachmentEditor(fieldName, fieldValue, resolvedType.DefaultMimeType, out var mimeAttachmentEditor))
                {
                    return mimeAttachmentEditor;
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

    private static bool TryCreateLocalizedMimeAttachmentEditor(
        string fieldName,
        JsonElement fieldValue,
        string? defaultMimeType,
        out EntityFieldEditorViewModel editor)
    {
        editor = null!;
        if (fieldValue.ValueKind != JsonValueKind.Object || fieldValue.TryGetProperty("mime-type", out _))
        {
            return false;
        }

        var localizedValues = new List<LocalizedMimeAttachmentValueViewModel>();
        foreach (var property in fieldValue.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !TryCreateSingleMimeAttachmentEditor(fieldName, property.Value, defaultMimeType, out var localizedEditor))
            {
                localizedValues.Clear();
                break;
            }

            localizedValues.Add(new LocalizedMimeAttachmentValueViewModel(property.Name, localizedEditor));
        }

        if (localizedValues.Count == 0)
        {
            return false;
        }

        editor = new LocalizedMimeAttachmentFieldEditorViewModel(fieldName, localizedValues);
        return true;
    }

    private static bool TryCreateSingleMimeAttachmentEditor(
        string fieldName,
        JsonElement fieldValue,
        string? defaultMimeType,
        out MimeAttachmentFieldEditorViewModel editor)
    {
        editor = null!;
        if (fieldValue.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasMimeType = fieldValue.TryGetProperty("mime-type", out var mimeTypeElement)
            && mimeTypeElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(mimeTypeElement.GetString());
        if (!hasMimeType && string.IsNullOrWhiteSpace(defaultMimeType))
        {
            return false;
        }

        var mimeType = hasMimeType
            ? mimeTypeElement.GetString()!
            : defaultMimeType!;
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
        editor = string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
            ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url)
            : new PlainMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url);
        return true;
    }

    private static bool TryCreateMimeAttachmentEditor(
        string fieldName,
        JsonElement fieldValue,
        string? defaultMimeType,
        out EntityFieldEditorViewModel editor)
    {
        editor = null!;
        if (fieldValue.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryCreateLocalizedMimeAttachmentEditor(fieldName, fieldValue, defaultMimeType, out var localizedEditor))
        {
            editor = localizedEditor;
            return true;
        }

        var mimeAttachmentValue = fieldValue;
        if (!fieldValue.TryGetProperty("mime-type", out _)
            && fieldValue.TryGetProperty("default", out var defaultMimeAttachment)
            && defaultMimeAttachment.ValueKind == JsonValueKind.Object)
        {
            mimeAttachmentValue = defaultMimeAttachment;
        }

        var hasMimeType = mimeAttachmentValue.TryGetProperty("mime-type", out var mimeTypeElement)
            && mimeTypeElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(mimeTypeElement.GetString());
        if (!hasMimeType && string.IsNullOrWhiteSpace(defaultMimeType))
        {
            return false;
        }

        var mimeType = hasMimeType
            ? mimeTypeElement.GetString()!
            : defaultMimeType!;
        var textContent = mimeAttachmentValue.TryGetProperty("content", out var contentElement)
                          && contentElement.ValueKind == JsonValueKind.Object
                          && contentElement.TryGetProperty("text", out var textElement)
                          && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString()
            : null;
        var url = mimeAttachmentValue.TryGetProperty("url", out var urlElement)
                  && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        MimeAttachmentFieldEditorViewModel mimeEditor = string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
            ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url)
            : new PlainMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url);
        editor = new LocalizedMimeAttachmentFieldEditorViewModel(fieldName, mimeEditor);
        return true;
    }

    private static bool TryCreateJsonSchemaEditor(
        string fieldName,
        JsonElement fieldValue,
        out EntityFieldEditorViewModel editor)
    {
        editor = null!;
        if (!string.Equals(fieldName, "schema", StringComparison.Ordinal)
            || (fieldValue.ValueKind != JsonValueKind.Object && fieldValue.ValueKind != JsonValueKind.Array))
        {
            return false;
        }

        editor = new JsonSchemaFieldEditorViewModel(fieldName, fieldValue.GetRawText());
        return true;
    }
}
