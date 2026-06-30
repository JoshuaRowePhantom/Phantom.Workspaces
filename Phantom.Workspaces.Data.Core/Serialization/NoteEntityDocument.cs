using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data.Serialization;

public sealed record NoteEntityDocument
    : EntityDocumentBase
{
    [JsonPropertyName("content")]
    public Dictionary<string, MimeAttachmentDocument>? ContentByLocale { get; init; }

    [JsonPropertyName("title")]
    public Dictionary<string, string>? TitleByLocale { get; init; }

    public static string? TryReadDefaultMarkdownText(JsonElement? entityData)
    {
        if (entityData is not JsonElement entityDataElement
            || Deserialize(entityDataElement) is not NoteEntityDocument noteEntityDocument)
        {
            return null;
        }

        return noteEntityDocument.GetPreferredMarkdownText();
    }

    public static NoteEntityDocument? Deserialize(JsonElement entityData)
    {
        if (entityData.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return EntityJsonSerializer.Deserialize(entityData, EntitySerializationJsonContext.Default.NoteEntityDocument);
    }

    public string? GetPreferredMarkdownText()
    {
        if (this.ContentByLocale is null)
        {
            return null;
        }

        if (this.ContentByLocale.TryGetValue("default", out var defaultAttachment)
            && defaultAttachment.TryGetInlineMarkdownText(out var markdownText))
        {
            return markdownText;
        }

        foreach (var attachment in this.ContentByLocale.Values)
        {
            if (attachment.TryGetInlineMarkdownText(out markdownText))
            {
                return markdownText;
            }
        }

        return null;
    }

    public string? GetPreferredTitle(string? localeName = null)
    {
        if (this.TitleByLocale is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(localeName)
            && this.TitleByLocale.TryGetValue(localeName, out var localizedTitle)
            && !string.IsNullOrWhiteSpace(localizedTitle))
        {
            return localizedTitle;
        }

        return this.TitleByLocale.TryGetValue("default", out var defaultTitle)
            && !string.IsNullOrWhiteSpace(defaultTitle)
            ? defaultTitle
            : null;
    }
}

public sealed record MimeAttachmentDocument
{
    [JsonPropertyName("mime-type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("content")]
    public InlineContentDocument? Content { get; init; }

    public static MimeAttachmentDocument? Deserialize(JsonElement attachmentElement)
        => EntityJsonSerializer.Deserialize(
            attachmentElement,
            EntitySerializationJsonContext.Default.MimeAttachmentDocument);

    public bool TryGetInlineMarkdownText(out string markdownText)
    {
        markdownText = string.Empty;
        if (!string.Equals(this.MimeType, "text/markdown", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(this.Content?.Text))
        {
            return false;
        }

        markdownText = this.Content.Text;
        return true;
    }
}

public sealed record InlineContentDocument
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
