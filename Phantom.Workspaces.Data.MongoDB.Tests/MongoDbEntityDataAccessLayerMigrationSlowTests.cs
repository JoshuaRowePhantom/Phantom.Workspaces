using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Slow (Docker) tests for <see cref="MongoDbEntityDataAccessLayer.MigrateAsync"/>.
/// </summary>
[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbEntityDataAccessLayerMigrationSlowTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbEntityDataAccessLayerMigrationSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetCollectionAsync().GetAwaiter().GetResult();
    }

    private MongoDbEntityDataAccessLayer CreateDataAccessLayer()
        => new(_fixture.Database, MongoDbTestDatabaseFixture.EntityCollectionName);

    private IMongoCollection<BsonDocument> GetEntityCollection()
        => _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_entities");

    /// <summary>
    /// Inserts a raw document that simulates a pre-#760 entity: has current.data.names and
    /// current.data.entity-types but lacks current.name-parent-prefixes and current.participant-ids.
    /// </summary>
    private async Task<string> InsertPreV760DocumentAsync(
        string[] entityTypes,
        string[][] names)
    {
        var id = Guid.NewGuid().ToString();
        var collection = GetEntityCollection();

        var namesArray = new BsonArray(names.Select(n => new BsonArray(n.Select(s => (BsonValue)new BsonString(s)))));
        var typesArray = new BsonArray(entityTypes.Select(t => (BsonValue)new BsonString(t)));

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
                            { "entity-types", typesArray },
                            { "names", namesArray },
                        }
                    },
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "000000000000000000000000" },
                    // Deliberately omit name-parent-prefixes and participant-ids (pre-#760 doc)
                }
            },
        };

        await collection.InsertOneAsync(doc);
        return id;
    }

    /// <summary>
    /// Inserts a raw document that simulates a post-#760 (fix #760, now wrong) entity:
    /// has the old current.names and current.type-names fields that should be removed.
    /// </summary>
    private async Task<string> InsertPost760DocumentAsync(
        string[] entityTypes,
        string[][] names,
        string[][]? legacyNames = null,
        string[]? legacyTypeNames = null)
    {
        var id = Guid.NewGuid().ToString();
        var collection = GetEntityCollection();

        var namesArray = new BsonArray(names.Select(n => new BsonArray(n.Select(s => (BsonValue)new BsonString(s)))));
        var typesArray = new BsonArray(entityTypes.Select(t => (BsonValue)new BsonString(t)));

        var currentDoc = new BsonDocument
        {
            {
                "data", new BsonDocument
                {
                    { "entity-id", id },
                    { "entity-types", typesArray },
                    { "names", namesArray },
                }
            },
            { "is-deleted", false },
            { "modified-time-utc", DateTime.UtcNow },
            { "modified-version", "000000000000000000000000" },
            // Simulate already-migrated document (has the new fields)
            { "name-parent-prefixes", new BsonArray() },
            { "participant-ids", new BsonArray() },
        };

        if (legacyNames is not null)
        {
            currentDoc["names"] = new BsonArray(legacyNames.Select(n =>
                new BsonArray(n.Select(s => (BsonValue)new BsonString(s)))));
        }

        if (legacyTypeNames is not null)
        {
            currentDoc["type-names"] = new BsonArray(legacyTypeNames.Select(t => (BsonValue)new BsonString(t)));
        }

        var doc = new BsonDocument
        {
            { "_id", id },
            { "versions", new BsonArray() },
            { "current", currentDoc },
        };

        await collection.InsertOneAsync(doc);
        return id;
    }

    [Fact]
    public async Task MigrateAsync_BackfillsNameParentPrefixes_ForPreV760Documents()
    {
        // Entity with name ["computers", "hostname", "mypc"]
        // Expected name-parent-prefixes: [["computers"], ["computers","hostname"]]
        var id = await InsertPreV760DocumentAsync(
            entityTypes: ["entity"],
            names: [["computers", "hostname", "mypc"]]);

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var doc = await GetEntityCollection()
            .Find(new BsonDocument("_id", id))
            .FirstAsync();
        var current = doc["current"].AsBsonDocument;
        Assert.True(current.Contains("name-parent-prefixes"), "name-parent-prefixes must be backfilled");
        var prefixes = current["name-parent-prefixes"].AsBsonArray
            .Select(static p => p.AsBsonArray.Select(static s => s.AsString).ToArray())
            .ToArray();

        Assert.Contains(prefixes, static p => p.SequenceEqual(["computers"], StringComparer.Ordinal));
        Assert.Contains(prefixes, static p => p.SequenceEqual(["computers", "hostname"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task MigrateAsync_BackfillsParticipantIds_ForPreV760Documents()
    {
        var participantId1 = Guid.NewGuid().ToString();
        var participantId2 = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var collection = GetEntityCollection();

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
                            { "entity-types", new BsonArray { "entity", "relationship" } },
                            { "names", new BsonArray() },
                            {
                                "participants", new BsonDocument
                                {
                                    { "source", participantId1 },
                                    { "target", participantId2 },
                                }
                            },
                        }
                    },
                    { "is-deleted", false },
                    { "modified-time-utc", DateTime.UtcNow },
                    { "modified-version", "000000000000000000000000" },
                    // No name-parent-prefixes — pre-#760 doc
                }
            },
        };
        await collection.InsertOneAsync(doc);

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var migrated = await collection.Find(new BsonDocument("_id", id)).FirstAsync();
        var current = migrated["current"].AsBsonDocument;
        Assert.True(current.Contains("participant-ids"), "participant-ids must be backfilled");
        var participantIds = current["participant-ids"].AsBsonArray.Select(static v => v.AsString).ToHashSet();
        Assert.Contains(participantId1, participantIds, StringComparer.Ordinal);
        Assert.Contains(participantId2, participantIds, StringComparer.Ordinal);
    }

    [Fact]
    public async Task MigrateAsync_RemovesCurrentNamesAndTypeNames_FromPost760Documents()
    {
        // Insert a document that has the old (stale) current.names and current.type-names fields
        var id = await InsertPost760DocumentAsync(
            entityTypes: ["entity"],
            names: [["workspace", "main"]],
            legacyNames: [["workspace", "main"]],
            legacyTypeNames: ["entity"]);

        // Manually set name-parent-prefixes to absent so migration re-processes it
        // (Actually the test helper already sets it, so just verify $unset works on it)
        // Directly update to remove name-parent-prefixes so the doc looks like it needs migration
        var collection = GetEntityCollection();
        await collection.UpdateOneAsync(
            new BsonDocument("_id", id),
            new BsonDocument("$unset", new BsonDocument("current.name-parent-prefixes", "")));

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var doc = await collection.Find(new BsonDocument("_id", id)).FirstAsync();
        var current = doc["current"].AsBsonDocument;

        Assert.False(current.Contains("names"), "current.names must be removed by migration");
        Assert.False(current.Contains("type-names"), "current.type-names must be removed by migration");
        Assert.True(current.Contains("name-parent-prefixes"), "name-parent-prefixes must be set by migration");
    }

    [Fact]
    public async Task MigrateAsync_IsIdempotent_WhenRunTwice()
    {
        var id = await InsertPreV760DocumentAsync(
            entityTypes: ["entity"],
            names: [["workspace", "dev"]]);

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();

        // Run migration twice
        await dal.MigrateAsync();
        await dal.MigrateAsync();

        // Document should still be correct after second run
        var doc = await GetEntityCollection().Find(new BsonDocument("_id", id)).FirstAsync();
        var current = doc["current"].AsBsonDocument;
        Assert.True(current.Contains("name-parent-prefixes"), "name-parent-prefixes must be present after idempotent migration");
    }

    [Fact]
    public async Task MigrateAsync_ProcessesLargeCollection_InBatches()
    {
        // Insert more than 500 documents to exercise batch processing (500/batch)
        const int DocumentCount = 600;
        var collection = GetEntityCollection();
        var insertTasks = Enumerable.Range(0, DocumentCount).Select(i =>
            InsertPreV760DocumentAsync(
                entityTypes: ["entity"],
                names: [[$"workspace{i}"]]));

        foreach (var task in insertTasks)
        {
            await task;
        }

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        // All documents should now have name-parent-prefixes
        var stillMissing = await collection.CountDocumentsAsync(new BsonDocument
        {
            { "current.name-parent-prefixes", new BsonDocument("$exists", false) },
            { "current.is-deleted", new BsonDocument("$ne", true) },
        });

        Assert.Equal(0, stillMissing);
    }

    // ---- #1413: inline-versions migration ----

    private IMongoCollection<BsonDocument> GetVersionCollection()
        => _fixture.Database.GetCollection<BsonDocument>(
            $"{MongoDbTestDatabaseFixture.EntityCollectionName}_versions");

    /// <summary>
    /// Inserts a hardcoded old-shape (pre-#1411) entity document carrying an inline capital
    /// <c>Versions</c> array, as production/dev databases contain today.
    /// </summary>
    private async Task<(string Id, IReadOnlyList<(ObjectId VersionId, DateTime TimestampUtc, string Name)> Versions)>
        InsertOldShapeDocumentAsync(int versionCount, int paddingBytesPerVersion = 0)
    {
        var id = Guid.NewGuid().ToString();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var versions = new BsonArray();
        var meta = new List<(ObjectId, DateTime, string)>();
        BsonDocument? lastData = null;
        ObjectId lastVersionId = default;
        DateTime lastTimestamp = default;

        for (var i = 0; i < versionCount; i++)
        {
            var timestamp = baseTime.AddSeconds(i);
            var versionId = ObjectId.GenerateNewId(timestamp);
            var name = $"v{i}";
            var data = new BsonDocument
            {
                { "entity-id", id },
                { "entity-types", new BsonArray { "entity" } },
                { "names", new BsonArray { new BsonArray { name } } },
            };
            if (paddingBytesPerVersion > 0)
            {
                data["padding"] = new string('x', paddingBytesPerVersion);
            }

            versions.Add(new BsonDocument
            {
                { "VersionId", versionId },
                { "TimestampUtc", timestamp },
                { "data", data },
            });
            meta.Add((versionId, timestamp, name));
            lastData = (BsonDocument)data.DeepClone();
            lastVersionId = versionId;
            lastTimestamp = timestamp;
        }

        var doc = new BsonDocument
        {
            { "_id", id },
            { "Versions", versions },
            {
                "current", new BsonDocument
                {
                    { "data", (BsonValue?)lastData ?? BsonNull.Value },
                    { "is-deleted", false },
                    { "modified-time-utc", lastTimestamp },
                    { "modified-version", lastVersionId.ToString() },
                    { "name-parent-prefixes", new BsonArray() },
                    { "participant-ids", new BsonArray() },
                }
            },
        };

        await GetEntityCollection().InsertOneAsync(doc);
        return (id, meta);
    }

    [Fact]
    public async Task MigrateAsync_MovesInlineVersionsToVersionsCollection()
    {
        var (id, versions) = await InsertOldShapeDocumentAsync(versionCount: 3);

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var entityDoc = await GetEntityCollection().Find(new BsonDocument("_id", id)).FirstAsync();
        Assert.False(entityDoc.Contains("Versions"), "inline Versions array must be $unset after migration");

        var versionDocs = await GetVersionCollection()
            .Find(new BsonDocument("EntityId", id))
            .ToListAsync();
        Assert.Equal(versions.Count, versionDocs.Count);
        foreach (var expected in versions)
        {
            Assert.Contains(versionDocs, v => v["_id"].AsObjectId == expected.VersionId);
        }
    }

    [Fact]
    public async Task MigrateAsync_MigratesAllDocuments_WhenOldCollectionExists()
    {
        var expectedVersionCount = 0;
        for (var i = 0; i < 5; i++)
        {
            var (_, versions) = await InsertOldShapeDocumentAsync(versionCount: i + 1);
            expectedVersionCount += versions.Count;
        }

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var remainingOldShape = await GetEntityCollection()
            .CountDocumentsAsync(new BsonDocument("Versions", new BsonDocument("$exists", true)));
        Assert.Equal(0, remainingOldShape);

        var totalVersions = await GetVersionCollection().CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.Equal(expectedVersionCount, totalVersions);
    }

    [Fact]
    public async Task MigrateAsync_InlineVersions_IsIdempotent_WhenRunTwice()
    {
        var (id, versions) = await InsertOldShapeDocumentAsync(versionCount: 4);

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();

        await dal.MigrateAsync();
        await dal.MigrateAsync();

        var versionDocs = await GetVersionCollection()
            .CountDocumentsAsync(new BsonDocument("EntityId", id));
        Assert.Equal(versions.Count, versionDocs);

        var entityDoc = await GetEntityCollection().Find(new BsonDocument("_id", id)).FirstAsync();
        Assert.False(entityDoc.Contains("Versions"), "second run must be a no-op — the array stays removed");
    }

    [Fact]
    public async Task MigrateAsync_PreservesPointInTimeQueries_AfterVersionSplit()
    {
        var (id, versions) = await InsertOldShapeDocumentAsync(versionCount: 3);
        var entityId = new EntityId(id);

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        // History returns every migrated version in order.
        var history = await dal.GetHistoryAsync(new GetHistoryRequest { EntityIds = [entityId] });
        var entry = Assert.Single(history.History);
        Assert.Equal(
            versions.Select(static v => v.VersionId.ToString()).ToArray(),
            entry.UpdateTimes.Select(static t => t.ChangeId).ToArray());

        // Point-in-time reads resolve each historical version's data from the versions collection.
        foreach (var version in versions)
        {
            var getResult = await dal.GetAsync(new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [new Timestamp(new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero), version.VersionId.ToString())],
            });
            var snapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
            Assert.Contains($"\"{version.Name}\"", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MigrateAsync_ShrinksOversizedDocument_BelowBsonLimit()
    {
        // Seed a single entity document with a large inline Versions array (~8 MB).
        var (id, _) = await InsertOldShapeDocumentAsync(versionCount: 40, paddingBytesPerVersion: 200_000);

        var beforeDoc = await GetEntityCollection().Find(new BsonDocument("_id", id)).FirstAsync();
        var beforeSize = beforeDoc.ToBson().Length;
        Assert.True(beforeSize > 5_000_000, $"seed document should be multi-megabyte but was {beforeSize} bytes");

        var dal = CreateDataAccessLayer();
        await dal.EnsureIndexesAsync();
        await dal.MigrateAsync();

        var afterDoc = await GetEntityCollection().Find(new BsonDocument("_id", id)).FirstAsync();
        var afterSize = afterDoc.ToBson().Length;
        Assert.False(afterDoc.Contains("Versions"), "inline Versions array must be removed");
        Assert.True(afterSize < 1_000_000, $"migrated document should be far below 16 MB but was {afterSize} bytes");

        // The un-wedged document is writable again via the normal update path.
        var current = afterDoc["current"].AsBsonDocument;
        var tag = new ConcurrencyTag(current["modified-version"].AsString);
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{id}}",
              "entity-types": ["entity"],
              "names": [["rescued"]]
            }
            """);
        var updateResult = await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "rescue" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(id),
                    ConcurrencyTag = tag,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.Equal(UpdateState.Updated, Assert.Single(updateResult.EntityResults).UpdateState);
    }
}
