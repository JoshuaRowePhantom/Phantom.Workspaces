using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Json.Schema;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class StatusBadgeProjectorTests
{
    [Fact]
    public async Task ProjectAsync_AnnotatedStatusField_ProducesSingleBadge()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [EntityTypeReference("task")] = ParseSchema(
                """
                {
                  "properties": {
                    "status": {
                      "type": "string",
                      "x-field-status": {
                        "good-status-values": ["completed"],
                        "bad-status-values": ["blocked", "cancelled"]
                      }
                    }
                  }
                }
                """),
        };

        using var entity = JsonDocument.Parse(
            """
            { "entity-types": ["entity", "task"], "status": "completed" }
            """);

        var badges = await ProjectAsync(schemas, entity.RootElement);

        var badge = Assert.Single(badges);
        Assert.Equal("completed", badge.StatusValue);
        Assert.Equal("Theme.Status.Good", badge.BrushKey);
        Assert.Equal("status: completed", badge.Tooltip);
    }

    [Fact]
    public async Task ProjectAsync_MultipleAnnotatedStatusFields_ProducesBadgePerField()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [EntityTypeReference("pull-request")] = ParseSchema(
                """
                {
                  "properties": {
                    "status": {
                      "type": "string",
                      "x-field-status": { "good-status-values": ["completed"], "bad-status-values": ["abandoned"] }
                    },
                    "merge-status": {
                      "type": "string",
                      "x-field-status": { "good-status-values": ["succeeded"], "bad-status-values": ["conflicts"] }
                    }
                  }
                }
                """),
        };

        using var entity = JsonDocument.Parse(
            """
            { "entity-types": ["entity", "pull-request"], "status": "active", "merge-status": "conflicts" }
            """);

        var badges = await ProjectAsync(schemas, entity.RootElement);

        Assert.Equal(2, badges.Count);
        var statusBadge = Assert.Single(badges, badge => badge.StatusValue == "active");
        Assert.StartsWith("Theme.Status.Palette.", statusBadge.BrushKey);
        Assert.Equal("status: active", statusBadge.Tooltip);
        var mergeBadge = Assert.Single(badges, badge => badge.StatusValue == "conflicts");
        Assert.Equal("Theme.Status.Bad", mergeBadge.BrushKey);
        Assert.Equal("merge-status: conflicts", mergeBadge.Tooltip);
    }

    [Fact]
    public async Task ProjectAsync_IncludesStatusFieldsFromAllEntityTypes()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [EntityTypeReference("type-a")] = ParseSchema(
                """
                {
                  "properties": {
                    "status-a": {
                      "type": "string",
                      "x-field-status": { "good-status-values": ["completed"], "bad-status-values": [] }
                    }
                  }
                }
                """),
            [EntityTypeReference("type-b")] = ParseSchema(
                """
                {
                  "properties": {
                    "status-b": {
                      "type": "string",
                      "x-field-status": { "good-status-values": [], "bad-status-values": ["blocked"] }
                    }
                  }
                }
                """),
        };

        using var entity = JsonDocument.Parse(
            """
            { "entity-types": ["entity", "type-a", "type-b"], "status-a": "completed", "status-b": "blocked" }
            """);

        var badges = await ProjectAsync(schemas, entity.RootElement);

        Assert.Equal(2, badges.Count);
        var badgeA = Assert.Single(badges, badge => badge.Tooltip == "status-a: completed");
        Assert.Equal("Theme.Status.Good", badgeA.BrushKey);
        var badgeB = Assert.Single(badges, badge => badge.Tooltip == "status-b: blocked");
        Assert.Equal("Theme.Status.Bad", badgeB.BrushKey);
    }

    [Fact]
    public async Task ProjectAsync_UnannotatedStringField_ProducesNoBadge()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [EntityTypeReference("task")] = ParseSchema(
                """
                {
                  "properties": {
                    "title": { "type": "string" }
                  }
                }
                """),
        };

        using var entity = JsonDocument.Parse(
            """
            { "entity-types": ["entity", "task"], "title": "do the thing" }
            """);

        var badges = await ProjectAsync(schemas, entity.RootElement);

        Assert.Empty(badges);
    }

    [Fact]
    public async Task ProjectAsync_AnnotatedFieldWithMissingOrEmptyValue_ProducesNoBadge()
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [EntityTypeReference("task")] = ParseSchema(
                """
                {
                  "properties": {
                    "status": {
                      "type": "string",
                      "x-field-status": { "good-status-values": ["completed"], "bad-status-values": [] }
                    }
                  }
                }
                """),
        };

        using var missing = JsonDocument.Parse(
            """
            { "entity-types": ["entity", "task"] }
            """);
        using var empty = JsonDocument.Parse(
            """
            { "entity-types": ["entity", "task"], "status": "" }
            """);

        Assert.Empty(await ProjectAsync(schemas, missing.RootElement));
        Assert.Empty(await ProjectAsync(schemas, empty.RootElement));
    }

    private static Task<IReadOnlyList<StatusBadgeModel>> ProjectAsync(
        IReadOnlyDictionary<string, JsonElement> schemasByReference,
        JsonElement entityData)
    {
        var resolver = new FieldTypeResolver(new FakeSchemaAccessor(schemasByReference));
        return StatusBadgeProjector.ProjectAsync(resolver, new StatusColorSelector(), entityData);
    }

    private static string EntityTypeReference(string entityTypeName)
        => JsonSerializer.Serialize(new[] { "entity-types", entityTypeName });

    private static JsonElement ParseSchema(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return document.RootElement.Clone();
    }

    private sealed class FakeSchemaAccessor : ISchemaAccessor
    {
        private readonly IReadOnlyDictionary<string, JsonElement> schemasByReference;

        public FakeSchemaAccessor(
            IReadOnlyDictionary<string, JsonElement> schemasByReference)
        {
            this.schemasByReference = schemasByReference;
        }

        public Task<JsonElement?> ResolveSchemaByReferenceAsync(
            string schemaReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                this.schemasByReference.TryGetValue(schemaReference, out var schema)
                    ? schema
                    : (JsonElement?)null);
        }

        public Task<SchemaRegistry> BuildSchemaRegistryAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
