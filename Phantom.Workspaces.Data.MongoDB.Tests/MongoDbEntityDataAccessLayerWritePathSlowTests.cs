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

    // #1411: the versions collection is the authoritative store for history; the entity document must
    // no longer carry an inline Versions array.
    [Fact]
    public async Task MongoDbEntityDataAccessLayer_Update_WritesVersionToVersionsCollectionNotEntityDocument()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());

        var result = await UpdateAsync(dal, entityId, null, Data(entityId, "one"));
        var tag = Assert.Single(result.EntityResults).ConcurrencyTag;
        Assert.NotNull(tag);

        var entityDoc = await EntityCollection().Find(Builders<BsonDocument>.Filter.Eq("_id", entityId.ToString()))
            .FirstOrDefaultAsync();
        Assert.NotNull(entityDoc);
        Assert.False(entityDoc.Contains("Versions"), "entity document must not carry an inline Versions array");
        Assert.False(entityDoc.Contains("versions"), "entity document must not carry an inline versions array");

        var versionDocs = await VersionCollection()
            .Find(Builders<BsonDocument>.Filter.Eq("EntityId", entityId.ToString()))
            .ToListAsync();
        var versionDoc = Assert.Single(versionDocs);
        Assert.Equal(tag!.Value.Value, versionDoc["_id"].AsObjectId.ToString());
    }

    [Fact]
    public async Task MongoDbEntityDataAccessLayer_ManyUpdates_KeepEntityDocumentSmall()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());

        const int UpdateCount = 2000;
        ConcurrencyTag? tag = null;
        for (var i = 0; i < UpdateCount; i++)
        {
            var result = await UpdateAsync(dal, entityId, tag, Data(entityId, "entity", i));
            tag = Assert.Single(result.EntityResults).ConcurrencyTag;
            Assert.NotNull(tag);
        }

        var entityDoc = await EntityCollection().Find(Builders<BsonDocument>.Filter.Eq("_id", entityId.ToString()))
            .FirstOrDefaultAsync();
        Assert.NotNull(entityDoc);
        var documentSizeBytes = entityDoc.ToBson().Length;
        Assert.True(documentSizeBytes < 1_000_000,
            $"entity document should stay far below 16 MB but was {documentSizeBytes} bytes");

        var versionCount = await VersionCollection()
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("EntityId", entityId.ToString()));
        Assert.Equal(UpdateCount, versionCount);
    }

    // #1411: version-first ordering means a crash between the two writes leaves a reconcilable state —
    // the current pointer is never lost and the version is never lost.
    [Fact]
    public async Task MongoDbEntityDataAccessLayer_Update_IsAtomicAcrossCurrentAndVersions()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());

        var firstResult = await UpdateAsync(dal, entityId, null, Data(entityId, "one"));
        var firstTag = Assert.Single(firstResult.EntityResults).ConcurrencyTag;
        Assert.NotNull(firstTag);

        // Simulate a crash AFTER the version insert but BEFORE the current upsert by inserting an
        // orphan version document directly.
        var orphanVersionId = ObjectId.GenerateNewId(DateTime.UtcNow);
        await VersionCollection().InsertOneAsync(new BsonDocument
        {
            { "_id", orphanVersionId },
            { "EntityId", entityId.ToString() },
            { "TimestampUtc", DateTime.UtcNow },
            { "Data", new BsonDocument { { "entity-id", entityId.ToString() }, { "orphan", true } } },
        });

        // No lost current pointer: current still points at the committed first version.
        var entityDoc = await EntityCollection().Find(Builders<BsonDocument>.Filter.Eq("_id", entityId.ToString()))
            .FirstOrDefaultAsync();
        Assert.NotNull(entityDoc);
        Assert.Equal(firstTag!.Value.Value, entityDoc["current"]["modified-version"].AsString);

        // No lost version: both the committed and the orphan version documents are present.
        var versionCount = await VersionCollection()
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("EntityId", entityId.ToString()));
        Assert.Equal(2, versionCount);

        // The state is reconcilable: a subsequent well-formed update using the current pointer succeeds.
        var reconcileResult = await UpdateAsync(dal, entityId, firstTag, Data(entityId, "two"));
        Assert.Equal(UpdateState.Updated, Assert.Single(reconcileResult.EntityResults).UpdateState);

        var getResult = await dal.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
        });
        var snapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Contains("\"two\"", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MongoDbEntityDataAccessLayer_Update_PreservesConcurrencyTagSemantics()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var entityId = new EntityId(Guid.NewGuid());

        var createResult = await UpdateAsync(dal, entityId, null, Data(entityId, "one"));
        var tag = Assert.Single(createResult.EntityResults).ConcurrencyTag;
        Assert.NotNull(tag);

        // A stale (empty/wrong) tag is rejected using the latest VersionId sourced from the current doc.
        var staleResult = await UpdateAsync(dal, entityId, new ConcurrencyTag(ObjectId.GenerateNewId().ToString()), Data(entityId, "two"));
        var stale = Assert.Single(staleResult.EntityResults);
        Assert.Equal(UpdateState.Failed, stale.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, stale.ConcurrencyMatchState);

        // The matching tag is accepted and advances the concurrency tag.
        var updateResult = await UpdateAsync(dal, entityId, tag, Data(entityId, "two"));
        var updated = Assert.Single(updateResult.EntityResults);
        Assert.Equal(UpdateState.Updated, updated.UpdateState);
        Assert.NotEqual(tag!.Value.Value, updated.ConcurrencyTag!.Value.Value);
    }

    private static Task<UpdateResult> UpdateAsync(
        MongoDbEntityDataAccessLayer dal,
        EntityId entityId,
        ConcurrencyTag? concurrencyTag,
        JsonElement data)
        => dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = concurrencyTag,
                    Data = data,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

    private static JsonElement Data(EntityId entityId, string name, int? counter = null)
    {
        var counterField = counter is null ? string.Empty : $"\n  \"counter\": {counter},";
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",{{counterField}}
              "entity-types": ["entity"],
              "names": [["{{name}}"]]
            }
            """);
        return document.RootElement.Clone();
    }

    private IMongoCollection<BsonDocument> EntityCollection()
        => _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");

    private IMongoCollection<BsonDocument> VersionCollection()
        => _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_versions");
}
