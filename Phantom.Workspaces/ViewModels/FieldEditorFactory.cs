using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Factory for building field editors from entity data
/// </summary>
public sealed class FieldEditorFactory
{
    private readonly ISchemaAccessor schemaAccessor;
    private readonly FieldTypeResolver fieldTypeResolver;
    private readonly StatusColorSelector statusColorSelector = new();
    private readonly EntityTypeViewCatalog entityTypeViewCatalog;
    private readonly IReadOnlyDictionary<string, EntityTypeViewDefinition>? viewSpecificEntityTypeViews;
    private readonly IEntityReferenceSearch? entityReferenceSearch;
    private readonly Action<string>? openEntity;
    private readonly CustomFieldEditorActivator customFieldEditorActivator;

    public FieldEditorFactory(
        EntityBroker entityBroker,
        EntityTypeViewCatalog entityTypeViewCatalog,
        IReadOnlyDictionary<string, EntityTypeViewDefinition>? viewSpecificEntityTypeViews = null,
        IEntityReferenceSearch? entityReferenceSearch = null,
        Action<string>? openEntity = null)
    {
        this.schemaAccessor = new SchemaAccessor(entityBroker.EntityRepository.DataAccessLayer);
        this.fieldTypeResolver = new FieldTypeResolver(this.schemaAccessor);
        this.entityTypeViewCatalog = entityTypeViewCatalog;
        this.viewSpecificEntityTypeViews = viewSpecificEntityTypeViews;
        this.entityReferenceSearch = entityReferenceSearch;
        this.openEntity = openEntity;
        this.customFieldEditorActivator = new CustomFieldEditorActivator(
            message => System.Diagnostics.Debug.WriteLine(message));
    }

    public async Task<IReadOnlyCollection<EntityFieldEditorViewModel>> BuildFieldEditorsAsync(
        JsonElement entityData,
        string? entityTypeName = null,
        bool expandAll = false)
    {
        var typeNames = entityTypeName is null
            ? Array.Empty<string>()
            : new[] { entityTypeName };
        return await this.BuildFieldEditorsAsync(entityData, typeNames, expandAll).ConfigureAwait(false);
    }

    /// <summary>
    /// Composes field editors across all of the entity's non-abstract entity types (issue #1164).
    /// Each type's <c>entity-type-view</c> contributes its <c>fields</c> array; duplicate field paths
    /// are collapsed to the first-contributing type. The merged list is ordered by
    /// <see cref="FieldOrdering.ComputeKey"/> using the contributing type's <c>entity-display-order</c>,
    /// so a tool+note entity renders tool-contributed fields (<c>entity-display-order: 260</c>) before
    /// note-contributed fields (no <c>entity-display-order</c>, defaults last).
    /// </summary>
    public async Task<IReadOnlyCollection<EntityFieldEditorViewModel>> BuildFieldEditorsAsync(
        JsonElement entityData,
        IReadOnlyList<string> entityTypeNames,
        bool expandAll = false)
    {
        IReadOnlyList<IReadOnlyList<string>> fieldPaths;
        IReadOnlyDictionary<string, string?>? displayFormats = null;
        // Per-field contributing type (used for cross-type ordering).
        Dictionary<string, string>? contributingTypeByPath = null;
        // Per-field within-view index (used as fallback relative order so the entity-type-view's
        // fields array order is preserved as the intra-type priority).
        Dictionary<string, int>? relativeIndexByPath = null;

        if (expandAll)
        {
            // Entity-browser rendering: enumerate all schema fields, ignore views.
            var fieldNames = await this.fieldTypeResolver.EnumerateObjectFieldNamesAsync(
                entityData,
                Array.Empty<string>(),
                entityData).ConfigureAwait(false);
            fieldPaths = fieldNames.Select(name => (IReadOnlyList<string>)[name]).ToArray();
        }
        else if (entityTypeNames.Count == 0)
        {
            fieldPaths = Array.Empty<IReadOnlyList<string>>();
        }
        else if (entityTypeNames.Count == 1)
        {
            // Preserve the historical single-type behavior for callers that pass exactly one type.
            var singleResult = this.BuildSingleTypeContributions(entityTypeNames[0]);
            if (singleResult is { } single)
            {
                (fieldPaths, displayFormats, contributingTypeByPath, relativeIndexByPath) = single;
            }
            else
            {
                // View was registered but declared no `fields` array — fall back to all schema fields.
                var fieldNames = await this.fieldTypeResolver.EnumerateObjectFieldNamesAsync(
                    entityData,
                    Array.Empty<string>(),
                    entityData).ConfigureAwait(false);
                fieldPaths = fieldNames.Select(name => (IReadOnlyList<string>)[name]).ToArray();
            }
        }
        else
        {
            // Multi-type composition: merge each type's entity-type-view.fields, dedup by path,
            // preserving first-contributing type + intra-view index for ordering.
            (fieldPaths, displayFormats, contributingTypeByPath, relativeIndexByPath) =
                this.BuildMultiTypeContributions(entityTypeNames);
        }

        fieldPaths = await this.OrderFieldPathsAsync(
            entityData,
            fieldPaths,
            contributingTypeByPath,
            relativeIndexByPath).ConfigureAwait(false);

        var fieldEditorTasks = fieldPaths
            .Select(fieldPath => this.CreateFieldEditorAsync(entityData, fieldPath, displayFormats))
            .ToArray();

        var fieldEditors = await Task.WhenAll(fieldEditorTasks).ConfigureAwait(false);
        return fieldEditors;
    }

    private (IReadOnlyList<IReadOnlyList<string>> FieldPaths,
             IReadOnlyDictionary<string, string?>? DisplayFormats,
             Dictionary<string, string>? ContributingTypeByPath,
             Dictionary<string, int>? RelativeIndexByPath)?
        BuildSingleTypeContributions(string entityTypeName)
    {
        EntityTypeViewDefinition? entityTypeView = null;
        if (this.viewSpecificEntityTypeViews?.TryGetValue(entityTypeName, out entityTypeView) != true)
        {
            entityTypeView = this.entityTypeViewCatalog.GetEntityTypeView(entityTypeName);
        }

        if (entityTypeView?.Fields is { } viewFields)
        {
            var paths = viewFields.Select(f => f.FieldPath).ToArray();
            var formats = viewFields.ToDictionary(
                f => string.Join(".", f.FieldPath),
                f => f.DisplayFormat,
                StringComparer.Ordinal);
            var contributingType = new Dictionary<string, string>(StringComparer.Ordinal);
            var relativeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < viewFields.Count; i++)
            {
                var key = string.Join(".", viewFields[i].FieldPath);
                contributingType[key] = entityTypeName;
                relativeIndex[key] = i;
            }
            return (paths, formats, contributingType, relativeIndex);
        }

        if (entityTypeView is not null)
        {
            // View exists but omits `fields` — signal to the caller to fall back to all schema fields.
            return null;
        }

        return ((IReadOnlyList<IReadOnlyList<string>>)Array.Empty<IReadOnlyList<string>>(),
                (IReadOnlyDictionary<string, string?>?)null,
                (Dictionary<string, string>?)null,
                (Dictionary<string, int>?)null);
    }

    private (IReadOnlyList<IReadOnlyList<string>> FieldPaths,
             IReadOnlyDictionary<string, string?>? DisplayFormats,
             Dictionary<string, string>? ContributingTypeByPath,
             Dictionary<string, int>? RelativeIndexByPath)
        BuildMultiTypeContributions(IReadOnlyList<string> entityTypeNames)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var mergedPaths = new List<IReadOnlyList<string>>();
        var mergedFormats = new Dictionary<string, string?>(StringComparer.Ordinal);
        var contributingType = new Dictionary<string, string>(StringComparer.Ordinal);
        var relativeIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var typeName in entityTypeNames)
        {
            EntityTypeViewDefinition? entityTypeView = null;
            if (this.viewSpecificEntityTypeViews?.TryGetValue(typeName, out entityTypeView) != true)
            {
                entityTypeView = this.entityTypeViewCatalog.GetEntityTypeView(typeName);
            }
            if (entityTypeView?.Fields is not { } viewFields)
            {
                // Types without a registered entity-type-view (or without a `fields` array) simply
                // contribute nothing to the merged field list — the diagnosis explicitly allows a
                // tool+note entity to render tool chrome + note fields even when no tool view exists.
                continue;
            }

            for (var i = 0; i < viewFields.Count; i++)
            {
                var field = viewFields[i];
                var key = string.Join(".", field.FieldPath);
                if (!seenPaths.Add(key))
                {
                    // First contributing type wins for duplicate paths.
                    continue;
                }
                mergedPaths.Add(field.FieldPath);
                mergedFormats[key] = field.DisplayFormat;
                contributingType[key] = typeName;
                relativeIndex[key] = i;
            }
        }

        return (mergedPaths, mergedFormats, contributingType, relativeIndex);
    }

    /// <summary>
    /// Builds the status badges for an entity by scanning its fields (across all its entity types)
    /// for the <c>x-field-status</c> annotation. Returns one badge per annotated status field whose
    /// value is a non-empty string.
    /// </summary>
    public Task<IReadOnlyList<StatusBadgeModel>> BuildStatusBadgesAsync(
        JsonElement entityData,
        CancellationToken cancellationToken = default)
    {
        return StatusBadgeProjector.ProjectAsync(
            this.fieldTypeResolver,
            this.statusColorSelector,
            entityData,
            cancellationToken);
    }

    /// <summary>
    /// Orders the supplied field paths by the entity-editor field ordering: absolute-ordered fields
    /// (schema <c>x-absolute-entity-display-order</c>) first, then the remaining fields grouped by
    /// their contributing entity type's <c>entity-display-order</c>. When a per-field contributing
    /// type is supplied (multi-type composition), that type drives the grouping so a tool-contributed
    /// field renders in the tool group and a note-contributed field renders in the note group.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<string>>> OrderFieldPathsAsync(
        JsonElement entityData,
        IReadOnlyList<IReadOnlyList<string>> fieldPaths,
        IReadOnlyDictionary<string, string>? contributingTypeByPath = null,
        IReadOnlyDictionary<string, int>? relativeIndexByPath = null)
    {
        var primaryEntityTypeName = ReadPrimaryEntityTypeName(entityData);
        var typeDisplayOrders = new Dictionary<string, double?>(StringComparer.Ordinal);
        var keyed = new List<(IReadOnlyList<string> FieldPath, FieldOrderingKey Key)>(fieldPaths.Count);

        foreach (var fieldPath in fieldPaths)
        {
            var fieldName = fieldPath[^1];
            var fieldPathKey = string.Join(".", fieldPath);
            var fieldValue = ResolveFieldValue(entityData, fieldPath);
            var resolvedType = await this.fieldTypeResolver
                .ResolveFieldTypeAsync(entityData, fieldPath, fieldValue)
                .ConfigureAwait(false);

            var contributingType = contributingTypeByPath is not null
                    && contributingTypeByPath.TryGetValue(fieldPathKey, out var typeName)
                ? typeName
                : primaryEntityTypeName;

            if (!typeDisplayOrders.TryGetValue(contributingType, out var typeDisplayOrder))
            {
                typeDisplayOrder = await this.ResolveEntityTypeDisplayOrderAsync(contributingType)
                    .ConfigureAwait(false);
                typeDisplayOrders[contributingType] = typeDisplayOrder;
            }

            // If the field's schema does not carry an x-relative-entity-display-order, fall back to
            // the field's index inside the contributing entity-type-view.fields array so that array
            // order is preserved as the intra-type priority.
            var relativeOrder = resolvedType.RelativeEntityDisplayOrder;
            if (relativeOrder == 0
                && relativeIndexByPath is not null
                && relativeIndexByPath.TryGetValue(fieldPathKey, out var indexInView))
            {
                relativeOrder = indexInView;
            }

            var key = FieldOrdering.ComputeKey(
                string.Join(".", fieldPath),
                resolvedType.AbsoluteEntityDisplayOrder,
                relativeOrder,
                contributingType,
                typeDisplayOrder);
            keyed.Add((fieldPath, key));
        }

        return keyed.OrderBy(static item => item.Key).Select(static item => item.FieldPath).ToArray();
    }

    /// <summary>
    /// Resolves the contributing entity type's <c>entity-display-order</c> by looking up the
    /// entity-type schema entity by name. Returns <see langword="null"/> when no order is declared
    /// (which sorts that group last per <see cref="FieldOrdering.ComputeKey"/>).
    /// </summary>
    private async Task<double?> ResolveEntityTypeDisplayOrderAsync(string entityTypeName)
    {
        if (string.IsNullOrEmpty(entityTypeName))
        {
            return null;
        }

        var schemaEntity = await this.schemaAccessor
            .ResolveSchemaByReferenceAsync(entityTypeName)
            .ConfigureAwait(false);
        if (schemaEntity is not JsonElement schema
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("entity-display-order", out var order)
            || order.ValueKind != JsonValueKind.Number
            || !order.TryGetDouble(out var value))
        {
            return null;
        }

        return value;
    }

    private static string ReadPrimaryEntityTypeName(JsonElement entityData)
    {
        if (entityData.ValueKind == JsonValueKind.Object
            && entityData.TryGetProperty("entity-types", out var types)
            && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var type in types.EnumerateArray())
            {
                if (type.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(type.GetString()))
                {
                    return type.GetString()!;
                }
            }
        }

        return string.Empty;
    }

    private static JsonElement ResolveFieldValue(JsonElement rootEntity, IReadOnlyList<string> fieldPath)
    {
        var current = rootEntity;
        foreach (var component in fieldPath)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(component, out var next))
            {
                current = next;
            }
            else
            {
                using var nullDocument = JsonDocument.Parse("null");
                return nullDocument.RootElement.Clone();
            }
        }

        return current;
    }

    /// <summary>
    /// Navigates the entity data along the full field path (object property and array index segments)
    /// to obtain the field's value. Returns false when any segment cannot be resolved.
    /// </summary>
    private static bool TryNavigateToFieldValue(
        JsonElement rootEntity,
        IReadOnlyList<string> fieldPath,
        out JsonElement fieldValue)
    {
        var current = rootEntity;
        foreach (var segment in fieldPath)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out var next))
                {
                    fieldValue = default;
                    return false;
                }

                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array
                     && int.TryParse(segment, out var index)
                     && index >= 0
                     && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                fieldValue = default;
                return false;
            }
        }

        fieldValue = current;
        return true;
    }

    private async Task<EntityFieldEditorViewModel> CreateFieldEditorAsync(
        JsonElement rootEntity,
        IReadOnlyList<string> fieldPath,
        IReadOnlyDictionary<string, string?>? displayFormats = null)
    {
        var fieldName = fieldPath[^1];
        var fieldPathKey = string.Join(".", fieldPath);
        var displayFormat = displayFormats?.TryGetValue(fieldPathKey, out var format) == true ? format : null;
        var isInline = string.Equals(displayFormat, "inline", StringComparison.Ordinal);
        
        if (!TryNavigateToFieldValue(rootEntity, fieldPath, out var fieldValue))
        {
            using var nullDocument = JsonDocument.Parse("null");
            fieldValue = nullDocument.RootElement.Clone();
        }

        var resolvedType = await Task.Run(() => this.fieldTypeResolver.ResolveFieldTypeAsync(rootEntity, fieldPath, fieldValue));

        // 1. Custom editor selected by x-field-editor on the field/type schema.
        if (this.customFieldEditorActivator.TryCreate(
                resolvedType.FieldEditorTypeName,
                new FieldEditorContext(fieldName, fieldValue, resolvedType, this.entityReferenceSearch),
                out var customEditor)
            && customEditor is not null)
        {
            return customEditor;
        }

        // 2. Entity-list editor for a list of entity-id references (core.json entity-id-list).
        if (string.Equals(resolvedType.TypeName, "entity-id-list", StringComparison.Ordinal))
        {
            var entityIds = fieldValue.ValueKind == JsonValueKind.Array
                ? fieldValue.EnumerateArray()
                    .Where(static element => element.ValueKind == JsonValueKind.String)
                    .Select(static element => element.GetString() ?? string.Empty)
                    .ToArray()
                : Array.Empty<string>();
            var listEditor = new EntityListFieldEditorViewModel(
                fieldName,
                entityIds,
                resolvedType.EntityTypes,
                this.entityReferenceSearch,
                this.openEntity);
            await listEditor.ResolveDisplayNamesAsync().ConfigureAwait(true);
            return listEditor;
        }

        // 3. Entity-reference editor when the field's schema declares allowed entity types.
        if (resolvedType.EntityTypes.Count > 0)
        {
            var entityId = fieldValue.ValueKind == JsonValueKind.String ? fieldValue.GetString() : null;
            var referenceEditor = new EntityReferenceFieldEditorViewModel(
                fieldName,
                entityId,
                resolvedType.EntityTypes,
                this.entityReferenceSearch,
                this.openEntity);
            await referenceEditor.ResolveCurrentValueAsync().ConfigureAwait(true);
            return referenceEditor;
        }

        switch (resolvedType.TypeName)
        {
            case "boolean":
                return new BooleanToggleFieldEditorViewModel(
                    fieldName,
                    fieldValue.ValueKind == JsonValueKind.True);
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
                    if (TryCreateLocalizedMimeAttachmentEditor(fieldName, fieldValue, resolvedType.DefaultMimeType, isInline, out var localizedMimeEditor))
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
                        ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url, isInline)
                        : new PlainMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url, isInline);
                    return new LocalizedMimeAttachmentFieldEditorViewModel(fieldName, editor);
                }

                var defaultMimeType = resolvedType.DefaultMimeType ?? "application/octet-stream";
                MimeAttachmentFieldEditorViewModel defaultEditor = string.Equals(defaultMimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
                    ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, defaultMimeType, null, null, isInline)
                    : new PlainMimeAttachmentFieldEditorViewModel(fieldName, defaultMimeType, null, null, isInline);
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
                    items.Add(await this.CreateFieldEditorAsync(rootEntity, itemPath));
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
        bool isInline,
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
                || !TryCreateSingleMimeAttachmentEditor(fieldName, property.Value, defaultMimeType, isInline, out var localizedEditor))
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
        bool isInline,
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
            ? new MarkdownMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url, isInline)
            : new PlainMimeAttachmentFieldEditorViewModel(fieldName, mimeType, textContent, url, isInline);
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

        if (TryCreateLocalizedMimeAttachmentEditor(fieldName, fieldValue, defaultMimeType, isInline: false, out var localizedEditor))
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
        if (fieldValue.ValueKind != JsonValueKind.Object
            || !fieldValue.TryGetProperty("$schema", out var schemaElement)
            || schemaElement.ValueKind != JsonValueKind.String
            || !string.Equals(schemaElement.GetString(), "https://json-schema.org/draft/2020-12/schema", StringComparison.Ordinal))
        {
            return false;
        }

        editor = new JsonSchemaFieldEditorViewModel(fieldName, fieldValue.ToString());
        return true;
    }
}
