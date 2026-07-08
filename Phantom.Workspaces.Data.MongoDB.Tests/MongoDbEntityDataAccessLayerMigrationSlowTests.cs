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
}
