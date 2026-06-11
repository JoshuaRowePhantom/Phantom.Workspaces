using Phantom.Workspaces.Data.Serialization;
using System.Text.Json;

namespace Phantom.Workspaces.Data.Tests.Serialization;

public sealed class EntitySerializationTests
{
    [Fact]
    public void SchemaEntityDocument_TryParse_ExtractsNamesTypesAndSchemaId()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "f9473b48-c2b0-4f4e-bd4d-0ec41be1d5db",
              "entity-types": ["entity-type", "json-schema"],
              "names": [["entity-types", "note"], ["json-schemas", "https://schemas.example/note.json"]],
              "schema": {
                "$id": "https://schemas.example/note.json",
                "type": "object"
              }
            }
            """);

        var parsed = SchemaEntityDocument.TryParse(document.RootElement, out var schemaEntityDocument);

        Assert.True(parsed);
        Assert.NotNull(schemaEntityDocument);
        Assert.True(schemaEntityDocument.IsSchemaEntity());
        Assert.True(schemaEntityDocument.TryGetSchemaPayloadId(out var schemaPayloadId));
        Assert.Equal("https://schemas.example/note.json", schemaPayloadId);
        Assert.Equal("f9473b48-c2b0-4f4e-bd4d-0ec41be1d5db", schemaEntityDocument.EntityId);
        Assert.Contains("[\"entity-types\",\"note\"]", schemaEntityDocument.GetCanonicalNames());
        Assert.Contains("json-schema", schemaEntityDocument.GetExplicitEntityTypeNames());
    }

    [Fact]
    public void NoteEntityDocument_GetPreferredMarkdownText_PrefersDefaultLocale()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": {
                    "text": "# Default"
                  }
                },
                "en-US": {
                  "mime-type": "text/markdown",
                  "content": {
                    "text": "# English"
                  }
                }
              }
            }
            """);

        var parsed = NoteEntityDocument.TryParse(document.RootElement, out var noteEntityDocument);

        Assert.True(parsed);
        Assert.NotNull(noteEntityDocument);
        Assert.Equal("# Default", noteEntityDocument.GetPreferredMarkdownText());
    }

    [Fact]
    public void NoteEntityDocument_GetPreferredMarkdownText_ReadsDirectAttachmentShape()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "content": {
                "mime-type": "text/markdown",
                "content": {
                  "text": "# Direct"
                }
              }
            }
            """);

        var parsed = NoteEntityDocument.TryParse(document.RootElement, out var noteEntityDocument);

        Assert.True(parsed);
        Assert.NotNull(noteEntityDocument);
        Assert.Equal("# Direct", noteEntityDocument.GetPreferredMarkdownText());
    }
}
