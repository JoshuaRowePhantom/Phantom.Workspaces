using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Factory for building field editors from entity data
/// </summary>
internal sealed class FieldEditorFactory
{
    private readonly ISchemaAccessor schemaAccessor;
    private readonly FieldTypeResolver fieldTypeResolver;
    private readonly EntityTypeViewCatalog entityTypeViewCatalog;
    private readonly IReadOnlyDictionary<string, EntityTypeViewDefinition>? viewSpecificEntityTypeViews;

    public FieldEditorFactory(
        EntityBroker entityBroker,
        EntityTypeViewCatalog entityTypeViewCatalog,
        IReadOnlyDictionary<string, EntityTypeViewDefinition>? viewSpecificEntityTypeViews = null)
    {
        this.schemaAccessor = new SchemaAccessor(entityBroker.EntityRepository.DataAccessLayer);
        this.fieldTypeResolver = new FieldTypeResolver(this.schemaAccessor);
        this.entityTypeViewCatalog = entityTypeViewCatalog;
        this.viewSpecificEntityTypeViews = viewSpecificEntityTypeViews;
    }

    public async Task<IReadOnlyCollection<EntityFieldEditorViewModel>> BuildFieldEditorsAsync(
        JsonElement entityData,
        string? entityTypeName = null)
    {
        // Check if there's an entity-type-view that specifies which fields to show
        // Priority: view-specific > catalog > all fields
        EntityTypeViewDefinition? entityTypeView = null;
        if (entityTypeName is not null)
        {
            // Check view-specific entity-type-views first
            if (this.viewSpecificEntityTypeViews?.TryGetValue(entityTypeName, out entityTypeView) != true)
            {
                // Fall back to catalog
                entityTypeView = this.entityTypeViewCatalog.GetEntityTypeView(entityTypeName);
            }
        }

        IReadOnlyList<IReadOnlyList<string>> fieldPaths;
        IReadOnlyDictionary<string, string?>? displayFormats = null;
        
        if (entityTypeView?.Fields is { Count: > 0 } viewFields)
        {
            // Use fields specified in entity-type-view
            fieldPaths = viewFields.Select(f => f.FieldPath).ToArray();
            displayFormats = viewFields.ToDictionary(
                f => string.Join(".", f.FieldPath),
                f => f.DisplayFormat,
                StringComparer.Ordinal);
        }
        else
        {
            // Fall back to enumerating all fields from schema
            var fieldNames = await this.fieldTypeResolver.EnumerateObjectFieldNamesAsync(
                entityData,
                Array.Empty<string>(),
                entityData).ConfigureAwait(false);
            fieldPaths = fieldNames.Select(name => (IReadOnlyList<string>)[name]).ToArray();
        }

        var fieldEditorTasks = fieldPaths
            .Select(fieldPath => this.CreateFieldEditorAsync(entityData, fieldPath, displayFormats))
            .ToArray();

        var fieldEditors = await Task.WhenAll(fieldEditorTasks).ConfigureAwait(false);
        return fieldEditors;
    }

    private async Task<EntityFieldEditorViewModel> CreateFieldEditorAsync(
        JsonElement rootEntity,
        IReadOnlyList<string> fieldPath,
        IReadOnlyDictionary<string, string?>? displayFormats = null)
    {
        var fieldName = fieldPath[^1];
        var fieldPathKey = string.Join(".", fieldPath);
        var displayFormat = displayFormats?.TryGetValue(fieldPathKey, out var format) == true ? format : null;
        
        if (!rootEntity.TryGetProperty(fieldName, out var fieldValue))
        {
            using var nullDocument = JsonDocument.Parse("null");
            fieldValue = nullDocument.RootElement.Clone();
        }

        var resolvedType = await Task.Run(() => this.fieldTypeResolver.ResolveFieldTypeAsync(rootEntity, fieldPath, fieldValue));

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
