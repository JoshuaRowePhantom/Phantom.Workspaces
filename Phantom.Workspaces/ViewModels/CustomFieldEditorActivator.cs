using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Context supplied to a custom field editor factory when activating an editor.
/// </summary>
public sealed record FieldEditorContext(
    string FieldName,
    JsonElement FieldValue,
    ResolvedFieldType ResolvedType,
    IEntityReferenceSearch? EntityReferenceSearch);

/// <summary>
/// Resolves the editor view model selected by a field schema's <c>x-field-editor</c> keyword.
/// The value may be a registered short name (matched case-sensitively) or an assembly-qualified
/// type name of an <see cref="EntityFieldEditorViewModel"/> subtype. Unknown short names and
/// unloadable type names fall back to the built-in editor (the caller's default), logging the error.
/// </summary>
public sealed class CustomFieldEditorActivator
{
    private readonly Action<string>? logError;
    private readonly IReadOnlyDictionary<string, Func<FieldEditorContext, EntityFieldEditorViewModel>> shortNameFactories;

    public CustomFieldEditorActivator(Action<string>? logError = null)
    {
        this.logError = logError;
        this.shortNameFactories = BuildShortNameRegistry();
    }

    /// <summary>The set of registered short names. Exposed for completeness testing.</summary>
    public IReadOnlyCollection<string> RegisteredShortNames => (IReadOnlyCollection<string>)this.shortNameFactories.Keys;

    /// <summary>
    /// Attempts to create the custom editor selected by <paramref name="fieldEditorTypeName"/>.
    /// Returns <see langword="false"/> (and a null editor) when resolution fails so the caller can
    /// fall back to the default editor.
    /// </summary>
    public bool TryCreate(
        string? fieldEditorTypeName,
        FieldEditorContext context,
        out EntityFieldEditorViewModel? editor)
    {
        editor = null;
        if (string.IsNullOrWhiteSpace(fieldEditorTypeName))
        {
            return false;
        }

        // Short-name resolution first (case-sensitive: schema/data values are matched exactly).
        if (this.shortNameFactories.TryGetValue(fieldEditorTypeName, out var factory))
        {
            editor = factory(context);
            return true;
        }

        // Assembly-qualified fallback.
        var type = Type.GetType(fieldEditorTypeName, throwOnError: false);
        if (type is null || !typeof(EntityFieldEditorViewModel).IsAssignableFrom(type))
        {
            this.logError?.Invoke($"x-field-editor '{fieldEditorTypeName}' is not a registered short name or a loadable EntityFieldEditorViewModel type.");
            return false;
        }

        try
        {
            var instance = ActivateAssemblyQualified(type, context);
            if (instance is null)
            {
                this.logError?.Invoke($"x-field-editor type '{fieldEditorTypeName}' has no supported constructor.");
                return false;
            }

            editor = instance;
            return true;
        }
        catch (Exception exception)
        {
            this.logError?.Invoke($"Failed to construct x-field-editor type '{fieldEditorTypeName}': {exception.Message}");
            return false;
        }
    }

    private static EntityFieldEditorViewModel? ActivateAssemblyQualified(Type type, FieldEditorContext context)
    {
        // Prefer a (string fieldName, JsonElement value) constructor, then (string fieldName).
        if (type.GetConstructor([typeof(string), typeof(JsonElement)]) is not null)
        {
            return (EntityFieldEditorViewModel?)Activator.CreateInstance(type, context.FieldName, context.FieldValue);
        }

        if (type.GetConstructor([typeof(string)]) is not null)
        {
            return (EntityFieldEditorViewModel?)Activator.CreateInstance(type, context.FieldName);
        }

        return null;
    }

    private static IReadOnlyDictionary<string, Func<FieldEditorContext, EntityFieldEditorViewModel>> BuildShortNameRegistry()
    {
        return new Dictionary<string, Func<FieldEditorContext, EntityFieldEditorViewModel>>(StringComparer.Ordinal)
        {
            ["string"] = static context => new StringFieldEditorViewModel(context.FieldName, ValueAsString(context.FieldValue)),
            ["local-string"] = static context => CreateLocalString(context),
            ["mime-attachment"] = static context => CreateMimeAttachment(context, forceMarkdown: false),
            ["markdown"] = static context => CreateMimeAttachment(context, forceMarkdown: true),
            ["json-schema"] = static context => new JsonSchemaFieldEditorViewModel(context.FieldName, context.FieldValue.ToString()),
            ["entity-reference"] = static context => new EntityReferenceFieldEditorViewModel(
                context.FieldName,
                ValueAsEntityId(context.FieldValue),
                context.ResolvedType.EntityTypes,
                context.EntityReferenceSearch),
        };
    }

    private static EntityFieldEditorViewModel CreateLocalString(FieldEditorContext context)
    {
        var value = context.FieldValue;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var localizedValues = value.EnumerateObject()
                .Where(static property => property.Value.ValueKind == JsonValueKind.String)
                .Select(property => new LocalizedTextValueViewModel(property.Name, property.Value.GetString() ?? string.Empty))
                .ToArray();
            if (localizedValues.Length > 0)
            {
                return new LocalStringFieldEditorViewModel(context.FieldName, localizedValues);
            }
        }

        return new LocalStringFieldEditorViewModel(context.FieldName, ValueAsString(value));
    }

    private static EntityFieldEditorViewModel CreateMimeAttachment(FieldEditorContext context, bool forceMarkdown)
    {
        var value = context.FieldValue;
        var mimeType = forceMarkdown
            ? "text/markdown"
            : value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("mime-type", out var mimeTypeElement)
                && mimeTypeElement.ValueKind == JsonValueKind.String
                ? mimeTypeElement.GetString()!
                : context.ResolvedType.DefaultMimeType ?? "text/markdown";

        string? textContent = null;
        string? url = null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.Object
                && contentElement.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                textContent = textElement.GetString();
            }

            if (value.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
            {
                url = urlElement.GetString();
            }
        }

        MimeAttachmentFieldEditorViewModel editor = string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase)
            ? new MarkdownMimeAttachmentFieldEditorViewModel(context.FieldName, mimeType, textContent, url)
            : new PlainMimeAttachmentFieldEditorViewModel(context.FieldName, mimeType, textContent, url);
        return new LocalizedMimeAttachmentFieldEditorViewModel(context.FieldName, editor);
    }

    private static string ValueAsString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static string? ValueAsEntityId(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
