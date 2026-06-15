using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.Core.Tests;

public sealed class SchemaDenormalizerTests
{
    private const string WorkspaceDalSchemaId =
        "https://schemas.workspaces.phantom.to/workspaces/data/core/workspace-entities-data-access-layer.json";

    [Fact]
    public void Denormalize_BundlesCrossDocumentAndInternalRefs()
    {
        var resolver = new FakeSchemaDocumentResolver(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://x/a.json"] =
                """
                {
                  "$id": "https://x/a.json",
                  "$defs": {
                    "root": {
                      "type": "object",
                      "properties": {
                        "child": { "$ref": "b.json#/$defs/thing" },
                        "self": { "$ref": "#/$defs/root" }
                      }
                    }
                  }
                }
                """,
            ["https://x/b.json"] =
                """
                {
                  "$id": "https://x/b.json",
                  "$defs": { "thing": { "type": "string" } }
                }
                """,
        });
        var denormalizer = new SchemaDenormalizer(resolver);

        var schema = denormalizer.Denormalize("https://x/a.json#/$defs/root");

        // Root keeps its shape but with internal references only.
        var childRef = schema.GetProperty("properties").GetProperty("child").GetProperty("$ref").GetString();
        var selfRef = schema.GetProperty("properties").GetProperty("self").GetProperty("$ref").GetString();
        Assert.StartsWith("#/$defs/", childRef, StringComparison.Ordinal);
        Assert.StartsWith("#/$defs/", selfRef, StringComparison.Ordinal);

        // Both definitions are bundled; recursion terminates via an internal reference.
        var defs = schema.GetProperty("$defs");
        Assert.True(defs.TryGetProperty(childRef!["#/$defs/".Length..], out var thingDef));
        Assert.Equal("string", thingDef.GetProperty("type").GetString());
        Assert.True(defs.TryGetProperty(selfRef!["#/$defs/".Length..], out _));

        AssertNoExternalReferences(schema);
    }

    [Fact]
    public void Denormalize_RealGetRequest_IsSelfSufficient()
    {
        var resolver = new EmbeddedSchemaDocumentResolver(typeof(IDataAccessLayer).Assembly);
        var denormalizer = new SchemaDenormalizer(resolver);

        var schema = denormalizer.Denormalize($"{WorkspaceDalSchemaId}#/$defs/get-request");

        var json = schema.GetRawText();
        Assert.Contains("get-entity", json, StringComparison.Ordinal);
        Assert.True(schema.TryGetProperty("$defs", out _));
        AssertNoExternalReferences(schema);
    }

    [Fact]
    public void Denormalize_RealUpdateRequest_IsSelfSufficient()
    {
        var resolver = new EmbeddedSchemaDocumentResolver(typeof(IDataAccessLayer).Assembly);
        var denormalizer = new SchemaDenormalizer(resolver);

        var schema = denormalizer.Denormalize($"{WorkspaceDalSchemaId}#/$defs/update-request");

        AssertNoExternalReferences(schema);
        // No leftover meta keywords that tie the schema to an external resource.
        Assert.DoesNotContain("\"$id\"", schema.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"$schema\"", schema.GetRawText(), StringComparison.Ordinal);
    }

    private static void AssertNoExternalReferences(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        foreach (var reference in CollectReferences(node))
        {
            Assert.StartsWith("#/$defs/", reference, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> CollectReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Key == "$ref" && property.Value is JsonValue value && value.TryGetValue<string>(out var reference))
                    {
                        yield return reference;
                    }
                    else
                    {
                        foreach (var nested in CollectReferences(property.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in CollectReferences(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private sealed class FakeSchemaDocumentResolver : ISchemaDocumentResolver
    {
        private readonly IReadOnlyDictionary<string, JsonObject> documentsById;

        public FakeSchemaDocumentResolver(IReadOnlyDictionary<string, string> documentsById)
        {
            var parsed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var (id, json) in documentsById)
            {
                parsed[id] = (JsonObject)JsonNode.Parse(json)!;
            }

            this.documentsById = parsed;
        }

        public JsonObject? ResolveDocument(string documentId)
            => this.documentsById.TryGetValue(documentId, out var document) ? document : null;
    }
}
