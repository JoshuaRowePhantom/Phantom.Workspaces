using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Phantom.Workspaces.Data.Vector;

/// <summary>
/// Produces a deterministic, normalized, text-only projection of an entity for embedding.
/// String content is collected in document order; binary / non-text MIME content (objects whose
/// <c>mime-type</c> is not a text type, and <c>data:</c> URIs) is stripped, because the current
/// embeddings model only embeds text.
/// </summary>
public static class EntityTextProjection
{
    /// <summary>Projects an entity snapshot into an <see cref="EmbeddingInput"/>.</summary>
    public static EmbeddingInput Project(EntitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new EmbeddingInput
        {
            EntityId = snapshot.EntityId,
            Text = ProjectText(snapshot.Data),
        };
    }

    /// <summary>Projects an entity data document into normalized embedding text.</summary>
    public static string ProjectText(JsonElement? data)
    {
        if (data is not { } element)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        Collect(element, builder);
        return Normalize(builder.ToString());
    }

    private static void Collect(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value) && !IsLikelyBinary(value))
                {
                    builder.Append(value).Append('\n');
                }

                break;

            case JsonValueKind.Object:
                if (IsNonTextMimeObject(element))
                {
                    // Keep sibling text (for example a display-name) but drop the binary payload.
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("content") || property.NameEquals("data"))
                        {
                            continue;
                        }

                        Collect(property.Value, builder);
                    }
                }
                else
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        Collect(property.Value, builder);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, builder);
                }

                break;

            // Numbers, booleans and null contribute no embedding text.
        }
    }

    private static bool IsNonTextMimeObject(JsonElement objectElement)
        => objectElement.TryGetProperty("mime-type", out var mimeType)
            && mimeType.ValueKind == JsonValueKind.String
            && !IsTextMime(mimeType.GetString());

    private static bool IsTextMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return false;
        }

        return mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mime.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mime.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyBinary(string value)
        => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
