using System.Text.Json;
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
