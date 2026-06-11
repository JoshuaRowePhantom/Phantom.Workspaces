using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data.Serialization;

public sealed record NoteEntityDocument
    : EntityDocumentBase
{
    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    public static bool TryParse(JsonElement entityData, out NoteEntityDocument? noteEntityDocument)
    {
        if (entityData.ValueKind != JsonValueKind.Object)
        {
            noteEntityDocument = null;
            return false;
        }

        return EntityJsonSerializer.TryDeserialize(entityData, out noteEntityDocument);
    }

    public string? GetPreferredMarkdownText()
    {
        if (this.Content is not JsonElement contentElement)
        {
            return null;
        }

        if (MimeAttachmentDocument.TryParse(contentElement, out var directAttachment)
            && directAttachment.TryGetInlineMarkdownText(out var markdownText))
        {
            return markdownText;
        }

        if (!EntityJsonSerializer.TryDeserialize(contentElement, out Dictionary<string, MimeAttachmentDocument>? attachmentsByName)
            || attachmentsByName is null)
        {
            return null;
        }

        if (attachmentsByName.TryGetValue("default", out var defaultAttachment)
            && defaultAttachment.TryGetInlineMarkdownText(out markdownText))
        {
            return markdownText;
        }

        foreach (var attachment in attachmentsByName.Values)
        {
            if (attachment.TryGetInlineMarkdownText(out markdownText))
            {
                return markdownText;
            }
        }

        return null;
    }
}

public sealed record MimeAttachmentDocument
{
    [JsonPropertyName("mime-type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("content")]
    public InlineContentDocument? Content { get; init; }

    public static bool TryParse(JsonElement attachmentElement, out MimeAttachmentDocument attachmentDocument)
    {
        attachmentDocument = default!;
        return EntityJsonSerializer.TryDeserialize(attachmentElement, out attachmentDocument);
    }

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
