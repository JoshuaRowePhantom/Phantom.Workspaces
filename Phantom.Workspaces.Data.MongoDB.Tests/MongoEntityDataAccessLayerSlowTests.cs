using System.Text.Json;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoTestDatabaseCollection.CollectionName)]
public sealed class MongoEntityDataAccessLayerSlowTests : DataAccessLayerNonQueryWithoutHistoryTests
{
    private static new readonly EntityId SampleEntityId = new("b17c2f1a-98fb-4e59-9902-f86af1f0f6a9");

    private readonly MongoTestDatabaseFixture _fixture;

    public MongoEntityDataAccessLayerSlowTests(
        MongoTestDatabaseFixture fixture)
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

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new MongoEntityDataAccessLayer(_fixture.Database, MongoTestDatabaseFixture.EntityCollectionName);
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
}
