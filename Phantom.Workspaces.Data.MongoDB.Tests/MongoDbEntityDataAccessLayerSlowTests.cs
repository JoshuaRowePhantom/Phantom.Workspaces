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
            { "_id", entityId.ToString() },
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
            { "_id", entityId.ToString() },
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
            { "_id", entityId.ToString() },
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

    // #1412: point-in-time reads resolve the bracketing version from the versions collection.
    [Fact]
    public async Task MongoDbEntityDataAccessLayer_GetAtTimestamp_ResolvesVersionFromVersionsCollection()
    {
        await _fixture.ResetCollectionAsync();
        var dal = CreateDataAccessLayer();
        var entityId = new EntityId(Guid.NewGuid());

        var v1 = await UpdateEntityAsync(dal, entityId, null, "one");
        var v2 = await UpdateEntityAsync(dal, entityId, v1.ConcurrencyTag, "two");

        var atV1 = await GetAtAsync(dal, entityId, v1.ModifiedTime);
        Assert.Contains("\"one\"", atV1.Data?.GetRawText(), StringComparison.Ordinal);

        var atV2 = await GetAtAsync(dal, entityId, v2.ModifiedTime);
        Assert.Contains("\"two\"", atV2.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MongoDbEntityDataAccessLayer_GetHistory_ReturnsAllVersionsFromVersionsCollection()
    {
        await _fixture.ResetCollectionAsync();
        var dal = CreateDataAccessLayer();
        var entityId = new EntityId(Guid.NewGuid());

        var v1 = await UpdateEntityAsync(dal, entityId, null, "one");
        var v2 = await UpdateEntityAsync(dal, entityId, v1.ConcurrencyTag, "two");
        var v3 = await UpdateEntityAsync(dal, entityId, v2.ConcurrencyTag, "three");

        var history = await dal.GetHistoryAsync(new GetHistoryRequest { EntityIds = [entityId] });
        var entry = Assert.Single(history.History);
        Assert.Equal(entityId, entry.EntityId);
        Assert.Equal(3, entry.UpdateTimes.Count);
        Assert.Equal(
            new[] { v1.ModifiedTime.ChangeId, v2.ModifiedTime.ChangeId, v3.ModifiedTime.ChangeId },
            entry.UpdateTimes.Select(static t => t.ChangeId).ToArray());
    }

    [Fact]
    public async Task MongoDbEntityDataAccessLayer_Export_StreamsVersionsFromVersionsCollection()
    {
        await _fixture.ResetCollectionAsync();
        var dal = CreateDataAccessLayer();
        var entityId = new EntityId(Guid.NewGuid());

        var v1 = await UpdateEntityAsync(dal, entityId, null, "one");
        var v2 = await UpdateEntityAsync(dal, entityId, v1.ConcurrencyTag, "two");
        var v3 = await UpdateEntityAsync(dal, entityId, v2.ConcurrencyTag, "three");

#pragma warning disable CS0618 // ExportAsync is intentionally exercised here to verify the versions-collection stream.
        var all = await dal.ExportAsync(new ExportRequest());
        Assert.Equal(3, all.ChangeBatches.Count);
        Assert.Equal(
            new[] { v1.ModifiedTime.ChangeId, v2.ModifiedTime.ChangeId, v3.ModifiedTime.ChangeId },
            all.ChangeBatches.Select(static b => b.ChangeTime.ChangeId).ToArray());

        var fromV2 = await dal.ExportAsync(new ExportRequest { SnapshotTime = v2.ModifiedTime });
#pragma warning restore CS0618
        Assert.Equal(
            new[] { v2.ModifiedTime.ChangeId, v3.ModifiedTime.ChangeId },
            fromV2.ChangeBatches.Select(static b => b.ChangeTime.ChangeId).ToArray());
    }

    [Fact]
    public async Task MongoDbEntityDataAccessLayer_GetChangedEntities_UsesVersionsCollection()
    {
        await _fixture.ResetCollectionAsync();
        var dal = CreateDataAccessLayer();
        var entityId = new EntityId(Guid.NewGuid());

        var v1 = await UpdateEntityAsync(dal, entityId, null, "one");
        var v2 = await UpdateEntityAsync(dal, entityId, v1.ConcurrencyTag, "two");

        var changedSinceV1 = await dal.GetChangedEntitiesAsync(new GetChangedEntitiesRequest
        {
            EntityIdTimestamps = [new EntityIdTimestamp { EntityId = entityId, Timestamp = v1.ModifiedTime }],
        });
        var changed = Assert.Single(changedSinceV1.Entities);
        Assert.NotNull(changed.Entity);
        Assert.Contains("\"two\"", changed.Entity!.Data?.GetRawText(), StringComparison.Ordinal);

        var changedSinceV2 = await dal.GetChangedEntitiesAsync(new GetChangedEntitiesRequest
        {
            EntityIdTimestamps = [new EntityIdTimestamp { EntityId = entityId, Timestamp = v2.ModifiedTime }],
        });
        Assert.Empty(changedSinceV2.Entities);
    }

    // #1412: two versions sharing a TimestampUtc must be disambiguated by VersionId (_id ObjectId).
    [Fact]
    public async Task MongoDbEntityDataAccessLayer_GetAtExactTimestampTie_UsesVersionIdTieBreak()
    {
        await _fixture.ResetCollectionAsync();
        var fixedTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var dal = new MongoDbEntityDataAccessLayer(
            _fixture.Database,
            MongoDbTestDatabaseFixture.EntityCollectionName,
            timeProvider: new FixedTimeProvider(fixedTime));
        var entityId = new EntityId(Guid.NewGuid());

        var v1 = await UpdateEntityAsync(dal, entityId, null, "one");
        var v2 = await UpdateEntityAsync(dal, entityId, v1.ConcurrencyTag, "two");

        // Both versions share the fixed TimestampUtc but have distinct VersionIds.
        Assert.Equal(v1.ModifiedTime.DateTime, v2.ModifiedTime.DateTime);
        Assert.NotEqual(v1.ModifiedTime.ChangeId, v2.ModifiedTime.ChangeId);

        var atV1 = await GetAtAsync(dal, entityId, v1.ModifiedTime);
        Assert.Contains("\"one\"", atV1.Data?.GetRawText(), StringComparison.Ordinal);

        var atV2 = await GetAtAsync(dal, entityId, v2.ModifiedTime);
        Assert.Contains("\"two\"", atV2.Data?.GetRawText(), StringComparison.Ordinal);
    }

    private async Task<EntitySnapshot> UpdateEntityAsync(
        IDataAccessLayer dal,
        EntityId entityId,
        ConcurrencyTag? concurrencyTag,
        string name)
    {
        var result = await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "test" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = concurrencyTag,
                    Data = ParseEntityData(entityId, name),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var entityResult = Assert.Single(result.EntityResults);
        Assert.NotEqual(UpdateState.Failed, entityResult.UpdateState);
        Assert.NotNull(entityResult.CurrentEntity);
        return entityResult.CurrentEntity!;
    }

    private static async Task<EntitySnapshot> GetAtAsync(
        IDataAccessLayer dal,
        EntityId entityId,
        Timestamp timestamp)
    {
        var result = await dal.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
            Timestamps = [timestamp],
        });
        return Assert.Single(Assert.Single(result.Batches).Entities);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
                "entity-types": ["entity", "{{type}}"],
                "names": ["{{name}}"]
              }
              """);
        return document.RootElement.Clone();
    }

    private async Task<string> InsertPreV760EntityAsync(string[] entityTypes, string[][] names)
    {
        var id = Guid.NewGuid().ToString();
        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var nameComponents = names.Select(n => (BsonValue)new BsonArray(n.Select(s => (BsonValue)new BsonString(s))));
        var doc = new BsonDocument
        {
            { "_id", id },
            { "versions", new BsonArray() },
            {
                "current", new BsonDocument
                {
                    {
                        "data", new BsonDocument
                        {
                            { "entity-id", id },
                            { "entity-types", new BsonArray(entityTypes.Select(t => (BsonValue)new BsonString(t))) },
                            { "names", new BsonArray(nameComponents) },
                        }
                    },
                    { "type-names", new BsonArray(entityTypes.Select(t => (BsonValue)new BsonString(t))) },
                    { "names", new BsonArray(nameComponents) },
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "000000000000000000000000" },
                }
            },
        };
        await collection.InsertOneAsync(doc);
        return id;
    }

    private static JsonElement ParseEntityData(EntityId entityId, string[] entityTypes, string[][] names)
    {
        var typesJson = JsonSerializer.Serialize(entityTypes);
        var namesJson = JsonSerializer.Serialize(names);
        using var document = JsonDocument.Parse($$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": {{{typesJson}}},
              "names": {{{namesJson}}}
            }
            """);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseRelationshipData(EntityId entityId, string[] entityTypes, Dictionary<string, string> participants)
    {
        var typesJson = JsonSerializer.Serialize(entityTypes);
        var participantsJson = JsonSerializer.Serialize(participants);
        using var document = JsonDocument.Parse($$$"""
            {
              "entity-id": "{{{entityId}}}",
              "entity-types": {{{typesJson}}},
              "names": [],
              "participants": {{{participantsJson}}}
            }
            """);
        return document.RootElement.Clone();
    }

    private static async Task InsertEntityWithTimestampAsync(IMongoCollection<BsonDocument> collection, DateTime modifiedTimeUtc)
    {
        var id = Guid.NewGuid().ToString();
        var doc = new BsonDocument
        {
            { "_id", id },
            { "versions", new BsonArray() },
            {
                "current", new BsonDocument
                {
                    {
                        "data", new BsonDocument
                        {
                            { "entity-id", id },
                            { "entity-types", new BsonArray { "entity" } },
                            { "names", new BsonArray() },
                        }
                    },
                    { "name-parent-prefixes", new BsonArray() },
                    { "participant-ids", new BsonArray() },
                    { "is-deleted", false },
                    { "modified-time-utc", modifiedTimeUtc },
                    { "modified-version", ObjectId.GenerateNewId().ToString() },
                }
            },
        };
        await collection.InsertOneAsync(doc);
    }

    [Fact]
    public async Task GetAsync_ByEntityName_FindsPreV760Entity()
    {
        await _fixture.ResetCollectionAsync();
        var id = await InsertPreV760EntityAsync(["entity"], [["workspace", "test-entity"]]);
        var entityId = new EntityId(Guid.Parse(id));
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var result = await dal.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName("workspace", "test-entity"),
                    EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                },
            ],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(entityId, snapshot.EntityId);
    }

    [Fact]
    public async Task GetAsync_ByEntityType_FindsPreV760Entity()
    {
        await _fixture.ResetCollectionAsync();
        var id = await InsertPreV760EntityAsync(["entity", "task"], [["workspace", "typed-entity"]]);
        var entityId = new EntityId(Guid.Parse(id));
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var result = await dal.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityTypeNames = new EntityTypeNameSet(["task"]),
                },
            ],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(entityId, snapshot.EntityId);
    }

    [Fact]
    public async Task GetAsync_ByEntityName_EnumerateChildren_ReturnsDirectChildrenOnly()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var parentId = new EntityId(Guid.NewGuid());
        var childId = new EntityId(Guid.NewGuid());
        var grandchildId = new EntityId(Guid.NewGuid());

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = parentId, Data = ParseEntityData(parentId, ["entity"], [["parent"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = childId, Data = ParseEntityData(childId, ["entity"], [["parent", "child"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = grandchildId, Data = ParseEntityData(grandchildId, ["entity"], [["parent", "child", "grandchild"]]), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dal.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName("parent"),
                    EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                },
            ],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Contains("child", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ByEntityName_EnumerateAllChildren_ReturnsAllDescendants()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var parentId = new EntityId(Guid.NewGuid());
        var childId = new EntityId(Guid.NewGuid());
        var grandchildId = new EntityId(Guid.NewGuid());

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = parentId, Data = ParseEntityData(parentId, ["entity"], [["parent"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = childId, Data = ParseEntityData(childId, ["entity"], [["parent", "child"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = grandchildId, Data = ParseEntityData(grandchildId, ["entity"], [["parent", "child", "grandchild"]]), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dal.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName("parent"),
                    EnumerateChildren = EnumerateChildrenAction.EnumerateAllChildren,
                },
            ],
        });

        Assert.Equal(2, Assert.Single(result.Batches).Entities.Count);
    }

    [Fact]
    public async Task GetAsync_RelationshipsToReturn_UsesParticipantIdsIndex()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var entityId = new EntityId(Guid.NewGuid());
        var relId = new EntityId(Guid.NewGuid());

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = entityId, Data = ParseEntityData(entityId, ["entity"], [["test", "entity"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = relId, Data = ParseRelationshipData(relId, ["entity", "relationship"], new Dictionary<string, string> { ["entity"] = entityId.ToString() }), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dal.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId }],
            RelationshipsToReturn = [new GetRelationshipRequest()],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.NotEmpty(snapshot.Relationships);
    }

    [Fact]
    public async Task GetAsync_ByParticipantId_AndEntityType_FiltersCorrectly()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var entityId1 = new EntityId(Guid.NewGuid());
        var entityId2 = new EntityId(Guid.NewGuid());
        var rel1Id = new EntityId(Guid.NewGuid());
        var rel2Id = new EntityId(Guid.NewGuid());

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = entityId1, Data = ParseEntityData(entityId1, ["entity"], [["e1"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = entityId2, Data = ParseEntityData(entityId2, ["entity"], [["e2"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = rel1Id, Data = ParseRelationshipData(rel1Id, ["entity", "rel-type-a"], new Dictionary<string, string> { ["target"] = entityId1.ToString() }), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = rel2Id, Data = ParseRelationshipData(rel2Id, ["entity", "rel-type-b"], new Dictionary<string, string> { ["target"] = entityId2.ToString() }), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dal.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId1 }],
            RelationshipsToReturn = [new GetRelationshipRequest { RelationshipTypeNames = new RelationshipTypeNameSet(["rel-type-a"]) }],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(entityId1, snapshot.EntityId);
        var relationship = Assert.Single(snapshot.Relationships);
        Assert.Equal(rel1Id, relationship.EntityId);
    }

    [Fact]
    public async Task GetAsync_ByParticipantId_AndSpecificParticipantField_FiltersCorrectly()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var entityId1 = new EntityId(Guid.NewGuid());
        var someOtherId = new EntityId(Guid.NewGuid());
        var relId = new EntityId(Guid.NewGuid());

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = entityId1, Data = ParseEntityData(entityId1, ["entity"], [["e1"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = someOtherId, Data = ParseEntityData(someOtherId, ["entity"], [["e2"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = relId, Data = ParseRelationshipData(relId, ["entity", "link"], new Dictionary<string, string> { ["source"] = entityId1.ToString(), ["other"] = someOtherId.ToString() }), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dal.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = entityId1 }],
            RelationshipsToReturn = [new GetRelationshipRequest { RelationshipRoleNames = new RoleNameSet(["source"]) }],
        });

        var snapshot = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(entityId1, snapshot.EntityId);
        Assert.Single(snapshot.Relationships);
    }

    [Fact]
    public async Task ProcessQueueAsync_OrdersEntitiesByModifiedTime()
    {
        await _fixture.ResetCollectionAsync();
        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");

        var now = DateTime.UtcNow;

        // Insert in reverse order (newest first) to verify the DAL sorts by modified-time
        await InsertEntityWithTimestampAsync(collection, now);
        await InsertEntityWithTimestampAsync(collection, now.AddMinutes(-5));
        await InsertEntityWithTimestampAsync(collection, now.AddMinutes(-10));

        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        var result = await dal.ProcessQueueAsync(new ProcessQueueRequest { QueueName = "test-queue", Count = 10 });

        Assert.Equal(3, result.Entities.Count);
        for (var i = 0; i < result.Entities.Count - 1; i++)
        {
            Assert.True(
                result.Entities[i].ModifiedTime.DateTime <= result.Entities[i + 1].ModifiedTime.DateTime,
                $"Entity at index {i} should have modified time <= entity at index {i + 1}");
        }
    }

    [Fact]
    public async Task QueryAsync_EntityType_UsesCurrentDataEntityTypes()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        // Insert a raw BSON doc with current.type-names but WITHOUT current.data.entity-types
        var oldId = Guid.NewGuid().ToString();
        var entityCollection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var oldDoc = new BsonDocument
        {
            { "_id", oldId },
            { "versions", new BsonArray() },
            {
                "current", new BsonDocument
                {
                    {
                        "data", new BsonDocument
                        {
                            { "entity-id", oldId },
                            // Deliberately NO entity-types in data
                            { "names", new BsonArray { new BsonArray { "old", "entity" } } },
                        }
                    },
                    { "type-names", new BsonArray { "special-type" } },
                    { "name-parent-prefixes", new BsonArray() },
                    { "participant-ids", new BsonArray() },
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "000000000000000000000000" },
                }
            },
        };
        await entityCollection.InsertOneAsync(oldDoc);

        // Insert a proper doc with entity-types in data via UpdateAsync
        var newEntityId = new EntityId(Guid.NewGuid());
        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = newEntityId,
                    Data = ParseEntityData(newEntityId, ["entity", "special-type"], [["new", "entity"]]),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        var result = await dal.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("by-type"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["special-type"]) },
                },
            ],
        });

        var entities = Assert.Single(result.Batches).Entities;
        var returnedId = Assert.Single(entities).EntityId;
        Assert.Equal(newEntityId, returnedId);
    }

    [Fact]
    public async Task QueryAsync_EntityParticipation_ReturnsParticipantsOfLinkedRelationship()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var entityA = new EntityId(Guid.NewGuid());
        var entityB = new EntityId(Guid.NewGuid());
        var relId = new EntityId(Guid.NewGuid());

        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange { EntityId = entityA, Data = ParseEntityData(entityA, ["entity"], [["a"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = entityB, Data = ParseEntityData(entityB, ["entity"], [["b"]]), EntityChangeMode = EntityChangeMode.Replace },
                new EntityChange { EntityId = relId, Data = ParseRelationshipData(relId, ["entity", "linked", "relationship"], new Dictionary<string, string> { ["source"] = entityA.ToString(), ["target"] = entityB.ToString() }), EntityChangeMode = EntityChangeMode.Replace },
            ],
        });

        var result = await dal.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("participation"),
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(["linked"]),
                        ParticipationRoleNames = new RoleNameSet(["source"]),
                    },
                },
            ],
        });

        var entities = Assert.Single(result.Batches).Entities;
        Assert.Contains(entityA, entities.Select(e => e.EntityId));
    }

    [Fact]
    public async Task InitializeAsync_Succeeds_WhenPreV760EntitiesExistInMongoDB()
    {
        await _fixture.ResetCollectionAsync();
        var id1 = await InsertPreV760EntityAsync(["entity"], [["workspace", "entity-1"]]);
        var id2 = await InsertPreV760EntityAsync(["entity"], [["workspace", "entity-2"]]);
        var id3 = await InsertPreV760EntityAsync(["entity"], [["workspace", "entity-3"]]);

        var entityId1 = new EntityId(Guid.Parse(id1));
        var entityId2 = new EntityId(Guid.Parse(id2));
        var entityId3 = new EntityId(Guid.Parse(id3));

        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var result = await dal.GetAsync(new GetRequest
        {
            Entities =
            [
                new GetEntityRequest { EntityId = entityId1 },
                new GetEntityRequest { EntityId = entityId2 },
                new GetEntityRequest { EntityId = entityId3 },
            ],
        });

        var snapshots = result.Batches.SelectMany(b => b.Entities).ToList();
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.NotNull(snapshot.Data));
    }

    [Fact]
    public async Task EnsureIndexesAsync_CreatesAllFiveIndexes_WhenCollectionIsNew()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);
        await dal.EnsureIndexesAsync();

        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var indexList = await collection.Indexes.List().ToListAsync();
        var indexFields = indexList
            .SelectMany(index =>
            {
                if (index.TryGetValue("key", out var key) && key.IsBsonDocument)
                {
                    return key.AsBsonDocument.Names;
                }
                return Enumerable.Empty<string>();
            })
            .ToHashSet();

        Assert.Contains("current.data.entity-types", indexFields);
        Assert.Contains("current.data.names", indexFields);
        Assert.Contains("current.name-parent-prefixes", indexFields);
        Assert.Contains("current.participant-ids", indexFields);
        Assert.Contains("current.modified-time-utc", indexFields);
    }

    [Fact]
    public async Task EnsureIndexesAsync_IsIdempotent()
    {
        await _fixture.ResetCollectionAsync();
        var dal = new MongoDbEntityDataAccessLayer(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);

        await dal.EnsureIndexesAsync();

        var collection = _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");
        var indexCountAfterFirst = (await collection.Indexes.List().ToListAsync()).Count;

        await dal.EnsureIndexesAsync();

        var indexCountAfterSecond = (await collection.Indexes.List().ToListAsync()).Count;

        Assert.Equal(indexCountAfterFirst, indexCountAfterSecond);
    }
}
