using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbEntityDataAccessLayerWritePathSlowTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbEntityDataAccessLayerWritePathSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetCollectionAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task UpdateAsync_PopulatesParticipantIds_FromNestedParticipantsObject()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());
        var sourceId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();

        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "relationship"],
              "names": [],
              "participants": {
                "source": "{{sourceId}}",
                "target": "{{targetId}}"
              }
            }
            """);

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", entityId.ToString()))
            .FirstOrDefaultAsync();

        Assert.NotNull(doc);
        var current = doc["current"].AsBsonDocument;
        Assert.True(current.Contains("participant-ids"), "participant-ids should be present");
        var participantIds = current["participant-ids"].AsBsonArray.Select(v => v.AsString).ToList();
        Assert.Contains(sourceId, participantIds);
        Assert.Contains(targetId, participantIds);
    }

    [Fact]
    public async Task UpdateAsync_PopulatesNameParentPrefixes_FromEntityNames()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());

        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["a", "b", "c"]]
            }
            """);

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", entityId.ToString()))
            .FirstOrDefaultAsync();

        Assert.NotNull(doc);
        var current = doc["current"].AsBsonDocument;
        Assert.True(current.Contains("name-parent-prefixes"), "name-parent-prefixes should be present");
        var prefixes = current["name-parent-prefixes"].AsBsonArray
            .Select(v => v.AsBsonArray.Select(s => s.AsString).ToArray())
            .ToList();

        Assert.Contains(prefixes, p => p.SequenceEqual(new[] { "a" }));
        Assert.Contains(prefixes, p => p.SequenceEqual(new[] { "a", "b" }));
    }

    [Fact]
    public async Task UpdateAsync_NoCurrentNamesOrTypeNamesField()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());

        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["test", "entity"]]
            }
            """);

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", entityId.ToString()))
            .FirstOrDefaultAsync();

        Assert.NotNull(doc);
        var current = doc["current"].AsBsonDocument;
        Assert.False(current.Contains("names"), "Legacy 'names' field should not be present in current");
        Assert.False(current.Contains("type-names"), "Legacy 'type-names' field should not be present in current");
    }
}
