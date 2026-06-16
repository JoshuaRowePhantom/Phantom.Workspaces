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
