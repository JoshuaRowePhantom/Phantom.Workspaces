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
              "entity-types": ["entity", "entity-type", "json-schema"],
              "names": [["entity-types", "note"], ["json-schemas", "https://schemas.example/note.json"]],
              "schema": {
                "$id": "https://schemas.example/note.json",
                "type": "object"
              }
            }
            """);

        var schemaEntityDocument = SchemaEntityDocument.Deserialize(document.RootElement);

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
              "title": {
                "default": "Getting Started",
                "fr-FR": "Commencer"
              },
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

        var noteEntityDocument = NoteEntityDocument.Deserialize(document.RootElement);

        Assert.NotNull(noteEntityDocument);
        Assert.Equal("# Default", noteEntityDocument.GetPreferredMarkdownText());
        Assert.Equal("Getting Started", noteEntityDocument.GetPreferredTitle());
        Assert.Equal("Commencer", noteEntityDocument.GetPreferredTitle("fr-FR"));
    }

    [Fact]
    public void NoteEntityDocument_GetPreferredMarkdownText_UsesAnyAvailableLocaleWhenDefaultMissing()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "content": {
                "en-US": {
                  "mime-type": "text/markdown",
                  "content": {
                    "text": "# English"
                  }
                }
              }
            }
            """);

        var noteEntityDocument = NoteEntityDocument.Deserialize(document.RootElement);

        Assert.NotNull(noteEntityDocument);
        Assert.Equal("# English", noteEntityDocument.GetPreferredMarkdownText());
    }

    [Fact]
    public void NoteEntityDocument_TryReadDefaultMarkdownText_NullData_ReturnsNull()
    {
        Assert.Null(NoteEntityDocument.TryReadDefaultMarkdownText(null));
    }

    [Fact]
    public void NoteEntityDocument_TryReadDefaultMarkdownText_NonObjectElement_ReturnsNull()
    {
        using var document = JsonDocument.Parse("\"not-an-object\"");

        Assert.Null(NoteEntityDocument.TryReadDefaultMarkdownText(document.RootElement));
    }

    [Fact]
    public void NoteEntityDocument_TryReadDefaultMarkdownText_ValidMarkdownContent_ReturnsMarkdownText()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": {
                    "text": "# Hello"
                  }
                }
              }
            }
            """);

        Assert.Equal("# Hello", NoteEntityDocument.TryReadDefaultMarkdownText(document.RootElement));
    }

    [Fact]
    public void NoteEntityDocument_TryReadDefaultMarkdownText_NoMarkdownContent_ReturnsNull()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "content": {
                "default": {
                  "mime-type": "text/plain",
                  "content": {
                    "text": "plain text"
                  }
                }
              }
            }
            """);

        Assert.Null(NoteEntityDocument.TryReadDefaultMarkdownText(document.RootElement));
    }
}
