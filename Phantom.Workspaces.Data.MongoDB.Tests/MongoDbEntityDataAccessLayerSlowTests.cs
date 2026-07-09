using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbEntityDataAccessLayerSlowTests : DataAccessLayerNonQueryWithoutHistoryTests
{
    private static new readonly EntityId SampleEntityId = new("b17c2f1a-98fb-4e59-9902-f86af1f0f6a9");

    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbEntityDataAccessLayerSlowTests(
        MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetCollectionAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task UpdateAndGet_PersistsEntityInMongo()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();

        var createResult = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "create entity" },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = SampleEntityId,
                    Data = ParseEntityData(SampleEntityId, "one"),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var created = Assert.Single(createResult.EntityResults);
        Assert.Equal(UpdateState.Added, created.UpdateState);
        Assert.NotNull(created.ConcurrencyTag);

        var getResult = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityId = SampleEntityId,
                },
            ],
        });

        var snapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Equal(SampleEntityId, snapshot.EntityId);
        Assert.Contains("\"one\"", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWithoutConcurrencyTag_OnExistingEntity_Fails()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();

        var firstUpdate = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "create entity" },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = SampleEntityId,
                    Data = ParseEntityData(SampleEntityId, "one"),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var firstTag = Assert.Single(firstUpdate.EntityResults).ConcurrencyTag;
        Assert.NotNull(firstTag);

        var secondUpdate = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "update without tag" },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = SampleEntityId,
                    Data = ParseEntityData(SampleEntityId, "two"),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var failed = Assert.Single(secondUpdate.EntityResults);
        Assert.Equal(UpdateState.Failed, failed.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failed.ConcurrencyMatchState);
        Assert.Contains("Concurrency tag is required.", failed.Errors.Select(static error => error.Message));
    }

    [Fact]
    public async Task Delete_WithMatchingConcurrencyTag_RemovesEntityFromReads()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();

        var createResult = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "create entity" },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = SampleEntityId,
                    Data = ParseEntityData(SampleEntityId, "one"),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        var tag = Assert.Single(createResult.EntityResults).ConcurrencyTag;
        Assert.NotNull(tag);

        var deleteResult = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "delete entity" },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = SampleEntityId,
                    ConcurrencyTag = tag,
                    Data = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        Assert.Equal(UpdateState.Removed, Assert.Single(deleteResult.EntityResults).UpdateState);

        var getResult = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityId = SampleEntityId,
                },
            ],
        });

        var deletedSnapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Null(deletedSnapshot.Data);
    }

    [Fact]
    public async Task GetAsync_DocumentWithUnknownCurrentField_DoesNotThrow()
    {
        await _fixture.ResetCollectionAsync();
        var entityId = new EntityId("a1234567-89ab-cdef-0123-456789abcdef");
        
        // Insert a document directly into MongoDB with an unknown field in the 'current' subdocument
        // This simulates a document written by newer code (e.g., fix/787 with name-parent-prefixes)
        var collection = _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var document = new BsonDocument
        {
            { "_id", new ObjectId() },
            { "entity-id", entityId.ToString() },
            { "concurrency-tag", "initial-tag" },
            { "current", new BsonDocument
                {
                    { "data", new BsonDocument
                        {
                            { "entity-id", entityId.ToString() },
                            { "type-names", new BsonArray { "entity" } },
                            { "names", new BsonArray { "test-entity" } }
                        }
                    },
                    { "type-names", new BsonArray { "entity" } },
                    { "name-parent-prefixes", new BsonArray { "unknown-field-value" } }, // Unknown field
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "1" }
                }
            }
        };
        await collection.InsertOneAsync(document);

        // Now try to read it using the data access layer - should not throw FormatException
        var dataAccessLayer = CreateDataAccessLayer();
        var getResult = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityId = entityId,
                },
            ],
        });

        var snapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Equal(entityId, snapshot.EntityId);
    }

    [Fact]
    public async Task MongoDbCurrentProjection_BsonIgnoreExtraElements_IsApplied()
    {
        await _fixture.ResetCollectionAsync();
        
        // This test verifies that the [BsonIgnoreExtraElements] attribute is present
        // by directly deserializing a BSON document with extra fields
        var entityId = new EntityId("b2345678-9abc-def0-1234-56789abcdef0");
        
        var collection = _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var document = new BsonDocument
        {
            { "_id", new ObjectId() },
            { "entity-id", entityId.ToString() },
            { "concurrency-tag", "tag-1" },
            { "current", new BsonDocument
                {
                    { "data", BsonNull.Value },
                    { "type-names", new BsonArray { "entity" } },
                    { "embedding", BsonNull.Value },
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "1" },
                    { "extra-field-one", "should-be-ignored" },
                    { "extra-field-two", new BsonArray { 1, 2, 3 } },
                }
            }
        };
        await collection.InsertOneAsync(document);

        // Reading through the DAL should succeed without FormatException
        var dataAccessLayer = CreateDataAccessLayer();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await dataAccessLayer.GetAsync(new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest { EntityId = entityId },
                ],
            });
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetAsync_ById_ReturnsCorrectSnapshot()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();
        var otherId = new EntityId("c0ffee00-0000-0000-0000-000000000001");

        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = SampleEntityId, Data = ParseEntityData(SampleEntityId, "target"), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = otherId, Data = ParseEntityData(otherId, "other"), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = SampleEntityId }],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(SampleEntityId, snapshot.EntityId);
        Assert.Contains("\"target\"", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ByEntityType_ReturnsMatchingEntities()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();
        var entityId = new EntityId("d1000000-0000-0000-0000-000000000001");
        var noteId = new EntityId("d2000000-0000-0000-0000-000000000002");

        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = entityId, Data = ParseEntityData(entityId, "my-entity"), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = noteId, Data = ParseEntityDataWithType(noteId, "my-note", "note"), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityTypeNames = new EntityTypeNameSet(["note"]) }],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(noteId, snapshot.EntityId);
    }

    [Fact]
    public async Task GetAsync_ByEntityName_ReturnsCorrectSnapshot()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();
        var targetId = new EntityId("e1000000-0000-0000-0000-000000000001");
        var otherId = new EntityId("e2000000-0000-0000-0000-000000000002");

        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = targetId, Data = ParseEntityData(targetId, "alpha"), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = otherId, Data = ParseEntityData(otherId, "beta"), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName("alpha"),
                    EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                },
            ],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(targetId, snapshot.EntityId);
    }

    [Fact]
    public async Task GetAsync_EmptyEntities_ReturnsEmptyBatches()
    {
        await _fixture.ResetCollectionAsync();
        var dataAccessLayer = CreateDataAccessLayer();

        var result = await dataAccessLayer.GetAsync(new GetRequest { Entities = [] });

        Assert.Single(result.Batches);
        Assert.Empty(Assert.Single(result.Batches).Entities);
    }

    [Fact]
    public async Task MigrateAsync_ThenGetAsync_WithLegacyCurrentFields_Succeeds()
    {
        await _fixture.ResetCollectionAsync();
        
        // Insert a document with legacy schema (missing name-parent-prefixes and participant-ids)
        var entityId = new EntityId("f1234567-89ab-cdef-0123-456789abcdef");
        var collection = _fixture.Database.GetCollection<BsonDocument>($"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var document = new BsonDocument
        {
            { "_id", new ObjectId() },
            { "entity-id", entityId.ToString() },
            { "concurrency-tag", "legacy-tag" },
            { "current", new BsonDocument
                {
                    { "data", new BsonDocument
                        {
                            { "entity-id", entityId.ToString() },
                            { "type-names", new BsonArray { "entity" } },
                            { "names", new BsonArray { new BsonArray { "test", "legacy" } } }
                        }
                    },
                    { "type-names", new BsonArray { "entity" } },
                    { "names", new BsonArray { new BsonArray { "test", "legacy" } } }, // Legacy field
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "1" }
                }
            }
        };
        await collection.InsertOneAsync(document);

        // Call MigrateAsync to backfill new fields and remove old ones
        var mongoDataAccessLayer = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await mongoDataAccessLayer.MigrateAsync();

        // Now GetAsync should succeed and the document should have the new fields
        var getResult = await mongoDataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
        });

        var snapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Equal(entityId, snapshot.EntityId);
        
        // Verify the migrated document no longer has the legacy "names" field in current
        var migratedDoc = await collection.Find(new BsonDocument("entity-id", entityId.ToString())).FirstOrDefaultAsync();
        Assert.NotNull(migratedDoc);
        var currentBson = migratedDoc["current"].AsBsonDocument;
        Assert.False(currentBson.Contains("names"), "Legacy 'names' field should be removed");
        Assert.True(currentBson.Contains("name-parent-prefixes"), "New 'name-parent-prefixes' field should exist");
    }

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
    }

    private static JsonElement ParseEntityData(
        EntityId entityId,
        string name)
    {
        using var document = JsonDocument.Parse(
            $$"""
              {
                "entity-id": "{{entityId}}",
                "type-names": ["entity"],
                "names": ["{{name}}"]
              }
              """);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseEntityDataWithType(
        EntityId entityId,
        string name,
        string type)
    {
        using var document = JsonDocument.Parse(
            $$"""
              {
                "entity-id": "{{entityId}}",
                "type-names": ["entity", "{{type}}"],
                "names": ["{{name}}"]
              }
              """);
        return document.RootElement.Clone();
    }
}
