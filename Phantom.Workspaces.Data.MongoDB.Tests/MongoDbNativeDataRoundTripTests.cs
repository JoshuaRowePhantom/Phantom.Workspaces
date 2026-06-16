using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Xunit;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Verifies that entity data is stored as native BSON and round-trips faithfully against a real
/// Atlas Local container - including JSON Schema content with <c>$</c>-prefixed and dotted keys
/// (which is why data must be stored carefully) - and that the native field clause queries the
/// denormalized current data.
/// </summary>
[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbNativeDataRoundTripTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbNativeDataRoundTripTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetCollectionAsync().GetAwaiter().GetResult();
    }

    private MongoDbEntityDataAccessLayer CreateDataAccessLayer()
        => new(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);

    [Fact]
    public async Task DollarAndDottedKeys_RoundTripFaithfully()
    {
        var dataAccessLayer = CreateDataAccessLayer();
        var guid = Guid.NewGuid();
        // Data containing JSON-Schema-style $-prefixed keys and a dotted key.
        var json =
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": ["entity-type","note"],
              "names": [["entity-types","sample"]],
              "schema": {
                "$id": "https://example/sample.json",
                "$ref": "core.json#/$defs/entity-id",
                "$defs": { "thing": { "type": "string" } },
                "a.b": "dotted-key-value"
              }
            }
            """;

        using var document = JsonDocument.Parse(json);
        var expected = document.RootElement.Clone();

        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed schema-like entity" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(guid),
                    ConcurrencyTag = null,
                    Data = expected,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, static r => r.UpdateState == UpdateState.Failed);

        var read = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = new EntityId(guid) }],
            Timestamps = [null],
        });
        var snapshot = read.Batches.SelectMany(batch => batch.Entities).Single(entity => entity.EntityId == new EntityId(guid));

        Assert.NotNull(snapshot.Data);
        Assert.True(
            JsonElement.DeepEquals(expected, snapshot.Data!.Value),
            $"Round-trip mismatch. expected={expected.GetRawText()} actual={snapshot.Data!.Value.GetRawText()}");
    }

    [Fact]
    public async Task FieldClause_MatchesOnNativeCurrentData()
    {
        var dataAccessLayer = CreateDataAccessLayer();
        var matchId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await SeedAsync(dataAccessLayer, matchId, "high");
        await SeedAsync(dataAccessLayer, otherId, "low");

        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("field"),
                    Clause = new EntityFieldQueryClause
                    {
                        FieldPath = new FieldPath("priority"),
                        ComparisonOperator = FieldComparisonOperator.Equals,
                        Value = JsonSerializer.SerializeToElement("high"),
                    },
                },
            ],
        });

        var ids = result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.EntityId).ToHashSet();
        Assert.Contains(new EntityId(matchId), ids);
        Assert.DoesNotContain(new EntityId(otherId), ids);
    }

    [Fact]
    public async Task ParticipationClause_JoinsParticipantsOfTypedRelationship_FilteredByMustHave()
    {
        var dataAccessLayer = CreateDataAccessLayer();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var assignedTask = Guid.NewGuid();
        var otherTask = Guid.NewGuid();

        await SeedRawAsync(dataAccessLayer, user, """{ "entity-types": ["user"] }""");
        await SeedRawAsync(dataAccessLayer, otherUser, """{ "entity-types": ["user"] }""");
        await SeedRawAsync(dataAccessLayer, assignedTask, """{ "entity-types": ["task"], "display-name": { "default": "Mine" } }""");
        await SeedRawAsync(dataAccessLayer, otherTask, """{ "entity-types": ["task"], "display-name": { "default": "Theirs" } }""");
        await SeedRawAsync(dataAccessLayer, Guid.NewGuid(), $$"""{ "entity-types": ["assigned-to","relationship"], "participants": { "target": "{{assignedTask}}", "user": "{{user}}" } }""");
        await SeedRawAsync(dataAccessLayer, Guid.NewGuid(), $$"""{ "entity-types": ["assigned-to","relationship"], "participants": { "target": "{{otherTask}}", "user": "{{otherUser}}" } }""");

        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("assigned"),
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
                        ParticipationRoleNames = new RoleNameSet(["target"]),
                        MustHave = new EntityParticipationRequirement
                        {
                            ParticipationRoleNames = new RoleNameSet(["user"]),
                            Clause = new EntityFieldQueryClause
                            {
                                FieldPath = new FieldPath("entity-id"),
                                ComparisonOperator = FieldComparisonOperator.Equals,
                                Value = JsonSerializer.SerializeToElement(user.ToString()),
                            },
                        },
                    },
                },
            ],
        });

        var ids = result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.EntityId).ToHashSet();
        Assert.Contains(new EntityId(assignedTask), ids);
        Assert.DoesNotContain(new EntityId(otherTask), ids);
        Assert.DoesNotContain(new EntityId(user), ids);
    }

    private static async Task SeedRawAsync(MongoDbEntityDataAccessLayer dataAccessLayer, Guid id, string bodyJson)
    {
        using var body = JsonDocument.Parse(bodyJson);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", id);
            foreach (var property in body.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(id),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, static r => r.UpdateState == UpdateState.Failed);
    }

    private static async Task SeedAsync(MongoDbEntityDataAccessLayer dataAccessLayer, Guid id, string priority)
    {
        var json =
            $$"""
            {
              "entity-id": "{{id}}",
              "entity-types": ["note"],
              "names": [["notes","{{id}}"]],
              "priority": {{JsonSerializer.Serialize(priority)}}
            }
            """;
        using var document = JsonDocument.Parse(json);
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(id),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, static r => r.UpdateState == UpdateState.Failed);
    }
}
