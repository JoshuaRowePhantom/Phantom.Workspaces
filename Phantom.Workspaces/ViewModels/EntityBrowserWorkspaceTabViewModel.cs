using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private readonly CancellationTokenSource _rebuildCts = new();
    private readonly EntityCardViewResolver entityCardViewResolver = new();
    private readonly EntityBroker entityBroker;
    private readonly ISchemaAccessor schemaAccessor;
    private readonly FieldTypeResolver fieldTypeResolver;
    private readonly EntityReferenceSearch entityReferenceSearch;
    private readonly FieldEditorFactory? fieldEditorFactory;
    private readonly SubscribedGet rootSubscribedGet;
    private readonly EntityListViewModel entityList = new();
    private readonly Dictionary<string, SubscribedGet> subscribedGetsByPath = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingSubscriptions = new(StringComparer.Ordinal);
    // #1232: folders the user has expanded at least once. Their already-loaded child node view models
    // are preserved when the folder is later collapsed so that re-expanding is instant (no re-subscribe
    // or rebuild-from-scratch). Never-expanded folders are absent here and stay unmaterialized.
    private readonly HashSet<string> expandedFolderPaths = new(StringComparer.Ordinal);
    private readonly TaskScheduler foregroundScheduler;
    private bool isRebuilding;
    private bool isRebuildPending;

    public EntityBrowserWorkspaceTabViewModel(
        EntityBroker entityBroker,
        SubscribedGet subscribedGet,
        FieldEditorFactory? fieldEditorFactory = null,
        TaskScheduler? foregroundScheduler = null)
    {
        this.entityBroker = entityBroker;
        this.schemaAccessor = new SchemaAccessor(this.entityBroker.EntityRepository.DataAccessLayer);
        this.fieldTypeResolver = new FieldTypeResolver(this.schemaAccessor);
        this.entityReferenceSearch = new EntityReferenceSearch(this.entityBroker);
        this.fieldEditorFactory = fieldEditorFactory;
        // #1232: UI-collection mutations (SetItems) marshal back onto the foreground scheduler while
        // the heavy JSON parsing runs on the thread pool. Defaults to the current synchronization
        // context (the UI thread at construction time), matching the rest of the app's convention.
        this.foregroundScheduler = foregroundScheduler ?? TaskScheduler.FromCurrentSynchronizationContext();
        this.rootSubscribedGet = subscribedGet;
        this.rootSubscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
        this.entityList.Items.CollectionChanged += this.OnEntityListItemsCollectionChanged;
        _ = this.RebuildTreeAsync();
    }

    public EntityListViewModel EntityList => this.entityList;

    /// <summary>
    /// Find (Ctrl-F) session over this tab's <see cref="EntityList"/>. Bound by the view code-behind
    /// to a <c>bringIntoView</c> callback that calls <c>TreeViewItem.BringIntoView()</c>.
    /// </summary>
    public FindViewModel Find
    {
        get => this.find ??= new FindViewModel(this.entityList);
        internal set => this.SetProperty(ref this.find, value);
    }
    private FindViewModel? find;

    public override async ValueTask DisposeAsync()
    {
        this.rootSubscribedGet.Results.CollectionChanged -= this.OnSubscribedResultsChanged;
        foreach (var subscribedGet in this.subscribedGetsByPath.Values)
        {
            subscribedGet.Results.CollectionChanged -= this.OnSubscribedResultsChanged;
        }

        this.entityList.Items.CollectionChanged -= this.OnEntityListItemsCollectionChanged;
        foreach (var item in this.entityList.Items)
        {
            item.PropertyChanged -= this.OnItemPropertyChanged;
        }

        this._rebuildCts.Cancel();
        await base.DisposeAsync();
    }

    private void OnSubscribedResultsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        _ = this.RebuildTreeAsync();
    }

    private void OnEntityListItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null && e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
        {
            foreach (EntityListItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= this.OnItemPropertyChanged;
            }
        }

        if (e.NewItems is not null && e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
        {
            foreach (EntityListItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += this.OnItemPropertyChanged;
            }
        }
    }

    private async Task RebuildTreeAsync()
    {
        if (this._rebuildCts.IsCancellationRequested)
        {
            return;
        }

        if (this.isRebuilding)
        {
            this.isRebuildPending = true;
            return;
        }

        var ct = this._rebuildCts.Token;
        this.isRebuilding = true;
        try
        {
            do
            {
                this.isRebuildPending = false;
                ct.ThrowIfCancellationRequested();

                // Issue #1177: yield to the dispatcher between rebuild iterations so async
                // subscription completions (fire-and-forget from EnsureChildSubscription) can be
                // observed. Previously the per-entity await BuildFieldEditorsAsync provided this
                // scheduling gap implicitly; now that field-editor construction is deferred, we
                // must yield explicitly or the rebuild loop can starve the dispatcher.
                await Task.Yield();
                ct.ThrowIfCancellationRequested();

                var expansionStateByPath = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var item in this.entityList.Items)
                {
                    expansionStateByPath[item.ItemKey] = item.IsExpanded;
                }

                var rootChildren = await this.BuildChildrenAsync(Array.Empty<string>(), this.rootSubscribedGet.Results, expansionStateByPath, ct);
                ct.ThrowIfCancellationRequested();
                var items = this.BuildItems(this.rootSubscribedGet.Results, rootChildren, expansionStateByPath);
                // #1232: publish the collection mutation on the foreground scheduler.
                await Task.Factory.StartNew(
                    () => this.entityList.SetItems(items),
                    ct,
                    TaskCreationOptions.None,
                    this.foregroundScheduler);
            }
            while (this.isRebuildPending);
        }
        catch (OperationCanceledException)
        {
            // View model was disposed while a rebuild was in progress.
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
        IReadOnlyDictionary<string, bool> expansionStateByPath,
        CancellationToken ct)
    {
        // #1232: parse the (potentially large) subscription snapshot into immediate-child descriptors
        // on the thread pool. This is the heavy per-entity work (Snapshot.Data access, name parsing,
        // JsonSerializer.Serialize) that previously ran synchronously on the UI thread and froze the
        // GUI on open. Only the bounded node construction and subscription bookkeeping below run on
        // the UI thread. The live subscription collection is snapshotted here on the UI thread before
        // handing an immutable array to the thread pool, so the parse never enumerates a collection
        // that the UI thread may be mutating concurrently.
        var parentPathArray = parentPath.ToArray();
        var entitiesSnapshot = entities.ToArray();
        var descriptors = await Task.Run(
            () => ExtractImmediateChildDescriptors(parentPathArray, entitiesSnapshot),
            ct);

        var nodes = new List<EntityListNodeViewModel>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            ct.ThrowIfCancellationRequested();

            var capturedEntity = descriptor.Entity;
            var node = new EntityListNodeViewModel(
                capturedEntity,
                descriptor.NameComponents,
                descriptor.SortKey,
                cardViewName: this.entityCardViewResolver.ResolveViewName(capturedEntity, EntityCardViewResolver.RawViewName),
                fieldEditorFactory: this.fieldEditorFactory);
            node.Card.SetLazyFieldEditorBuilder(lazyCt => this.BuildFieldEditorsAsync(capturedEntity, lazyCt));
            node.SetExpansionChangedCallback(this.OnNodeExpansionChanged);

            // Subscribe to this child's children so we can (a) show the expand chevron via HasChildren
            // and (b) populate them when the user expands. Grandchildren of a collapsed folder are
            // neither materialized nor recursed into — the subscription only tells us HasChildren.
            this.EnsureChildSubscription(descriptor.NameComponents, descriptor.SortKey);

            if (this.subscribedGetsByPath.TryGetValue(descriptor.SortKey, out var childGet))
            {
                var immediateChildKeys = ExtractImmediateChildKeys(descriptor.NameComponents, childGet.Results);
                var hasChildren = immediateChildKeys.Count > 0;
                node.SetImmediateChildKeys(immediateChildKeys);
                node.SetHasChildren(hasChildren);

                var isExpanded = expansionStateByPath.TryGetValue(descriptor.SortKey, out var expanded) && expanded;
                if (isExpanded)
                {
                    // #1232: record that this folder has been expanded at least once so its loaded
                    // children survive a later collapse.
                    this.expandedFolderPaths.Add(descriptor.SortKey);
                }

                // Materialize immediate children when the folder is currently expanded OR was expanded
                // before and then collapsed (loaded). This keeps re-expansion instant. Folders that have
                // never been expanded stay unmaterialized, preserving the lazy-load / no-freeze guarantee.
                var materializeChildren = hasChildren
                    && (isExpanded || this.expandedFolderPaths.Contains(descriptor.SortKey));
                if (materializeChildren)
                {
                    // Recurse only into this folder's own expanded/loaded descendants; a collapsed-but-
                    // loaded folder retains its child node view models but does not force its descendants
                    // open.
                    node.SetChildren(await this.BuildChildrenAsync(descriptor.NameComponents, childGet.Results, expansionStateByPath, ct));
                }
                else
                {
                    node.SetChildren(Array.Empty<EntityListNodeViewModel>());
                }
            }
            else
            {
                // Subscription not ready yet; its Results.CollectionChanged will trigger a rebuild.
                node.SetChildren(Array.Empty<EntityListNodeViewModel>());
            }

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Pure, thread-pool-safe extraction of the immediate children of <paramref name="parentPath"/>
    /// from a subscription's entities. Returns one descriptor per distinct immediate-child path,
    /// ordered by sort key. Performs no view-model construction or shared-state mutation.
    /// </summary>
    private static IReadOnlyList<ChildNodeDescriptor> ExtractImmediateChildDescriptors(
        IReadOnlyList<string> parentPath,
        IReadOnlyCollection<SubscribedEntityViewModel> entities)
    {
        var descriptors = new Dictionary<string, ChildNodeDescriptor>(StringComparer.Ordinal);
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
                if (descriptors.ContainsKey(sortKey))
                {
                    continue;
                }

                descriptors.Add(sortKey, new ChildNodeDescriptor(entity, nameComponents, sortKey));
            }
        }

        return descriptors
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .ToArray();
    }

    /// <summary>
    /// The item keys of the immediate children of <paramref name="parentPath"/> present in
    /// <paramref name="entities"/>, ordered by key. Returns keys only — it constructs no node view
    /// models — so a collapsed folder can expose child metadata and its expand chevron without
    /// materializing its descendants (issue #1232).
    /// </summary>
    private static IReadOnlyList<string> ExtractImmediateChildKeys(
        IReadOnlyList<string> parentPath,
        IReadOnlyCollection<SubscribedEntityViewModel> entities)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
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
                if (nameComponents.Length == parentPath.Count + 1
                    && nameComponents.Take(parentPath.Count).SequenceEqual(parentPath, StringComparer.Ordinal))
                {
                    keys.Add(JsonSerializer.Serialize(nameComponents));
                }
            }
        }

        return keys.ToArray();
    }

    private readonly record struct ChildNodeDescriptor(
        SubscribedEntityViewModel Entity,
        IReadOnlyList<string> NameComponents,
        string SortKey);

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
        var childItemKeys = node.Children.Count > 0
            ? node.Children
                .OrderBy(static child => child.SortKey, StringComparer.Ordinal)
                .Select(static child => JsonSerializer.Serialize(child.NameComponents))
                .ToArray()
            : node.ImmediateChildKeys.ToArray();
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
            var ct = this._rebuildCts.Token;
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
                },
                ct);
            if (!ct.IsCancellationRequested)
            {
                subscribedGet.Results.CollectionChanged += this.OnSubscribedResultsChanged;
                this.subscribedGetsByPath[pathKey] = subscribedGet;
                _ = this.RebuildTreeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // View model was disposed before the subscription completed.
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

        // #1232: collapsing a folder must NOT dispose its descendants' subscriptions or drop their
        // already-loaded child node view models — doing so forces a re-subscribe + rebuild on
        // re-expand. Preserving them (see BuildChildrenAsync / expandedFolderPaths) keeps re-expansion
        // instant. Subscriptions are released together in DisposeAsync when the tab closes.
        _ = this.RebuildTreeAsync();
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
        SubscribedEntityViewModel entity,
        CancellationToken ct)
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

            editors.Add(await this.CreateFieldEditorAsync(entityData, fieldName, fieldValue, [fieldName], ct));
        }

        return editors;
    }

    private async Task<EntityFieldEditorViewModel> CreateFieldEditorAsync(
        JsonElement rootEntity,
        string fieldName,
        JsonElement fieldValue,
        IReadOnlyList<string> fieldPath,
        CancellationToken ct)
    {
        var resolvedType = await Task.Run(() => this.fieldTypeResolver.ResolveFieldTypeAsync(rootEntity, fieldPath, fieldValue), ct);

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
                    items.Add(await this.CreateFieldEditorAsync(rootEntity, $"[{itemIndex}]", item, itemPath, ct));
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
                            fieldPath.Concat([childFieldName]).ToArray(),
                            ct));
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
