using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.ViewModels;

public abstract class EntityFieldEditorViewModel
{
    protected EntityFieldEditorViewModel(
        string fieldName,
        string typeName)
    {
        this.FieldName = fieldName;
        this.TypeName = typeName;
    }

    public string FieldName { get; }

    public string TypeName { get; }
}

public sealed class StringFieldEditorViewModel : EntityFieldEditorViewModel
{
    public StringFieldEditorViewModel(
        string fieldName,
        string value)
        : base(fieldName, "string")
    {
        this.Value = value;
    }

    public string Value { get; }
}

public sealed class LocalStringFieldEditorViewModel : EntityFieldEditorViewModel
{
    public LocalStringFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<StringFieldEditorViewModel> localizedValues)
        : base(fieldName, "local-string")
    {
        this.LocalizedValues = localizedValues;
    }

    public IReadOnlyCollection<StringFieldEditorViewModel> LocalizedValues { get; }
}

public sealed class MimeAttachmentFieldEditorViewModel : EntityFieldEditorViewModel
{
    public MimeAttachmentFieldEditorViewModel(
        string fieldName,
        string mimeType,
        string? textContent,
        string? url)
        : base(fieldName, "mime-attachment")
    {
        this.MimeType = mimeType;
        this.TextContent = textContent;
        this.Url = url;
    }

    public string MimeType { get; }

    public string? TextContent { get; }

    public string? Url { get; }

    public bool IsMarkdown => this.MimeType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase);
}

public sealed class ObjectFieldEditorViewModel : EntityFieldEditorViewModel
{
    public ObjectFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<EntityFieldEditorViewModel> fields)
        : base(fieldName, "object")
    {
        this.Fields = fields;
    }

    public IReadOnlyCollection<EntityFieldEditorViewModel> Fields { get; }
}

public sealed class ArrayFieldEditorViewModel : EntityFieldEditorViewModel
{
    public ArrayFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<EntityFieldEditorViewModel> items)
        : base(fieldName, "array")
    {
        this.Items = items;
    }

    public IReadOnlyCollection<EntityFieldEditorViewModel> Items { get; }
}
