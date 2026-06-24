using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class IDataAccessLayerSerializationTests
{
    [Fact]
    public void Serialize_GetEntityRequest_UsesKebabCasePropertiesAndEnumValues()
    {
        var getEntityRequest = new GetEntityRequest
        {
            EntityId = new EntityId("11111111-1111-1111-1111-111111111111"),
            EntityName = new EntityName("views", "sessions"),
            EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
            EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
            Properties = ["display-name", "content.default.content.text"],
            RelationshipsToReturn =
            [
                new GetRelationshipRequest
                {
                    RelationshipTypeNames = new RelationshipTypeNameSet(["related"]),
                    RelationshipRoleNames = new RoleNameSet(["child"]),
                },
            ],
        };

        var jsonObject = JsonSerializer.SerializeToNode(getEntityRequest)!.AsObject();
        Assert.True(jsonObject.ContainsKey("entity-id"));
        Assert.True(jsonObject.ContainsKey("entity-name"));
        Assert.True(jsonObject.ContainsKey("enumerate-children"));
        Assert.True(jsonObject.ContainsKey("entity-type-names"));
        Assert.True(jsonObject.ContainsKey("properties"));
        Assert.True(jsonObject.ContainsKey("relationships-to-return"));
        Assert.Equal("children", jsonObject["enumerate-children"]!.GetValue<string>());
        Assert.False(jsonObject.ContainsKey("EntityId"));
        Assert.False(jsonObject.ContainsKey("EntityName"));
    }

    [Fact]
    public void Serialize_GetRequest_UsesGetEntityPropertyName()
    {
        var getRequest = new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityTypeNames = new EntityTypeNameSet(["agent-definition"]),
                },
            ],
            RelationshipsToReturn =
            [
                new GetRelationshipRequest
                {
                    RelationshipRoleNames = new RoleNameSet(["parent"]),
                },
            ],
            Properties = ["display-name"],
            Timestamps = [null],
        };

        var jsonObject = JsonSerializer.SerializeToNode(getRequest)!.AsObject();
        Assert.True(jsonObject.ContainsKey("get-entity"));
        Assert.True(jsonObject.ContainsKey("relationships-to-return"));
        Assert.True(jsonObject.ContainsKey("properties"));
        Assert.True(jsonObject.ContainsKey("timestamps"));
        Assert.False(jsonObject.ContainsKey("entities"));
    }

    [Fact]
    public void Serialize_UpdateRequest_UsesKebabCasePropertiesAndEnumValues()
    {
        var updateRequest = new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown
                {
                    Text = "Serialization test",
                },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId("22222222-2222-2222-2222-222222222222"),
                    ConcurrencyTag = new ConcurrencyTag("etag-1"),
                    Data = JsonDocument.Parse("""{ "entity-types": ["entity", "note"] }""").RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        };

        var jsonObject = JsonSerializer.SerializeToNode(updateRequest)!.AsObject();
        Assert.True(jsonObject.ContainsKey("update-metadata"));
        Assert.True(jsonObject.ContainsKey("changes"));
        var entityChangeObject = jsonObject["changes"]!.AsArray()[0]!.AsObject();
        Assert.True(entityChangeObject.ContainsKey("entity-id"));
        Assert.True(entityChangeObject.ContainsKey("concurrency-tag"));
        Assert.True(entityChangeObject.ContainsKey("entity-change-mode"));
        Assert.Equal("replace", entityChangeObject["entity-change-mode"]!.GetValue<string>());
    }

    [Fact]
    public void QueryRequest_WithPolymorphicClauses_RoundTripsAndUsesDiscriminator()
    {
        // The inbox query shape: entities that are the "target" of an "actionable" interest whose
        // "user" participant is the current user.
        var queryRequest = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("actionable"),
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(["actionable"]),
                        ParticipationRoleNames = new RoleNameSet(["target"]),
                        MustHave = new EntityParticipationRequirement
                        {
                            ParticipationRoleNames = new RoleNameSet(["user"]),
                            Clause = new EntityFieldQueryClause
                            {
                                FieldPath = new FieldPath("entity-id"),
                                ComparisonOperator = FieldComparisonOperator.Equals,
                                Value = JsonDocument.Parse("\"${USER}\"").RootElement.Clone(),
                            },
                        },
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(queryRequest, WebDataAccessJsonSerialization.Options);

        // The polymorphic discriminator and kebab-case enum value are present on the wire.
        Assert.Contains("\"clause-type\":\"entity-participation\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"clause-type\":\"entity-field\"", json, System.StringComparison.Ordinal);
        Assert.Contains("\"comparison-operator\":\"equals\"", json, System.StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<QueryRequest>(json, WebDataAccessJsonSerialization.Options);
        Assert.NotNull(roundTripped);
        var clause = Assert.IsType<EntityParticipationQueryClause>(Assert.Single(roundTripped!.Clauses).Clause);
        Assert.Equal(["actionable"], clause.RelationshipTypeNames.Values);
        Assert.Equal(["target"], clause.ParticipationRoleNames!.Value.Values);
        var mustHaveClause = Assert.IsType<EntityFieldQueryClause>(clause.MustHave!.Clause);
        Assert.Equal(["entity-id"], mustHaveClause.FieldPath.Components);
        Assert.Equal(FieldComparisonOperator.Equals, mustHaveClause.ComparisonOperator);
        Assert.Equal("${USER}", mustHaveClause.Value!.Value.GetString());
    }

    [Fact]
    public async Task Serialize_GetRequestShape_ValidatesWhenEmbeddedInViewSubView()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var schemaAccessor = new SchemaAccessor(dataAccessLayer);
        var dalSchemaReference =
            "https://schemas.workspaces.phantom.to/workspaces/data/core/workspace-entities-data-access-layer.json";
        var coreSchemaReference =
            "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json";
        var dalSchema = await schemaAccessor.ResolveSchemaByReferenceAsync(dalSchemaReference);
        var coreSchema = await schemaAccessor.ResolveSchemaByReferenceAsync(coreSchemaReference);
        Assert.NotNull(dalSchema);
        Assert.NotNull(coreSchema);

        var getRequest = new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName("views", "sessions"),
                    Properties = ["display-name"],
                },
            ],
            Properties = ["display-name"],
            Timestamps = [null],
        };

        var schemaRegistry = new SchemaRegistry();
        _ = JsonSchema.FromText(
            coreSchema!.Value.GetProperty("schema").GetRawText(),
            new BuildOptions
            {
                SchemaRegistry = schemaRegistry,
            },
            new Uri(coreSchemaReference, UriKind.Absolute));
        var schema = JsonSchema.FromText(
            dalSchema!.Value.GetProperty("schema").GetRawText(),
            new BuildOptions
            {
                SchemaRegistry = schemaRegistry,
            });
        using var getRequestDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(
                getRequest,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }));
        var evaluationResult = schema.Evaluate(
            getRequestDocument.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                PreserveDroppedAnnotations = true,
            });

        Assert.True(
            evaluationResult.IsValid,
            string.Join(" | ", GetDetailedValidationErrors(evaluationResult)));
    }

    private static async Task<IDataAccessLayer> CreatePopulatedDataAccessLayerAsync()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var schemaValidatingDataAccessLayer = new SchemaValidatingDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(schemaValidatingDataAccessLayer);
        var errors = await schemaPopulator.Populate();
        Assert.Empty(errors);
        return schemaValidatingDataAccessLayer;
    }

    private static IReadOnlyCollection<string> GetDetailedValidationErrors(
        EvaluationResults evaluation)
    {
        var messages = new List<string>();
        CollectEvaluationErrors(evaluation, messages);
        return messages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void CollectEvaluationErrors(
        EvaluationResults evaluation,
        ICollection<string> messages)
    {
        var nodeHasError = false;
        if (evaluation.Errors is { Count: > 0 })
        {
            var location = evaluation.InstanceLocation.ToString();
            foreach (var error in evaluation.Errors)
            {
                var keyword = string.IsNullOrWhiteSpace(error.Key) ? "schema" : error.Key;
                var pathPrefix = string.IsNullOrWhiteSpace(location) || location == "#"
                    ? string.Empty
                    : $" at '{location}'";
                messages.Add($"{keyword}{pathPrefix}: {error.Value}");
            }

            nodeHasError = true;
        }

        if (!nodeHasError && !evaluation.IsValid)
        {
            var instanceLocation = evaluation.InstanceLocation.ToString();
            var schemaLocation = evaluation.SchemaLocation?.ToString() ?? "<unknown-schema-location>";
            var instanceText = string.IsNullOrWhiteSpace(instanceLocation) || instanceLocation == "#"
                ? "$"
                : instanceLocation;
            messages.Add($"validation failed at instance '{instanceText}' against '{schemaLocation}'");
        }

        if (evaluation.Details is not { Count: > 0 })
        {
            return;
        }

        foreach (var detail in evaluation.Details.Where(static detail => !detail.IsValid))
        {
            CollectEvaluationErrors(detail, messages);
        }
    }
}
