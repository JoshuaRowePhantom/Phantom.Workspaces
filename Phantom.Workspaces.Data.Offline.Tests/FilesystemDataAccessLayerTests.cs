using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class FilesystemDataAccessLayerTests : DataAccessLayerNonQueryWithoutHistoryTests, IDisposable
{
    private readonly string repositoryPath = TestPathFactory.CreateIsolatedDirectory("filesystem");

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new FilesystemDataAccessLayer(this.repositoryPath);
    }

    [Fact]
    public async Task UpdateReplace_CreatesEntityJsonFile()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId(Guid.Parse("d0dc5498-f970-4ce4-97fc-f75284453a15"));

        var result = await dataAccessLayer.UpdateAsync(CreateUpdateRequest(this.CreateEntity(entityId, "one")));
        Assert.Equal(UpdateState.Added, Assert.Single(result.EntityResults).UpdateState);

        Assert.True(File.Exists(GetEntityPath(this.repositoryPath, entityId)));
    }

    [Fact]
    public async Task UpdateReplace_WithNullData_DeletesEntityJsonFile()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId(Guid.Parse("2ea08302-7e2f-4716-b478-8cba08f6db8c"));

        var createResult = await dataAccessLayer.UpdateAsync(CreateUpdateRequest(this.CreateEntity(entityId, "one")));
        var concurrencyTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var deleteResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = concurrencyTag,
                    Data = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                }));
        Assert.Equal(UpdateState.Removed, Assert.Single(deleteResult.EntityResults).UpdateState);

        Assert.False(File.Exists(GetEntityPath(this.repositoryPath, entityId)));
    }

    [Fact]
    public async Task UpdateReplace_RelationshipEntity_CreatesMarkerFilesForParticipants()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var relationshipEntityId = new EntityId(Guid.Parse("8c7679a1-9f2d-4f8d-b611-6496c38f3ac5"));
        var participantA = new EntityId(Guid.Parse("f2978104-2f69-4467-bd07-8efd48f8536f"));
        var participantB = new EntityId(Guid.Parse("3a01ed74-b4b8-4b4d-8a40-f32cf3ec2867"));

        var createRelationshipResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(this.CreateRelationshipEntity(relationshipEntityId, participantA, participantB)));
        Assert.Equal(UpdateState.Added, Assert.Single(createRelationshipResult.EntityResults).UpdateState);

        Assert.True(File.Exists(GetRelationshipMarkerPath(this.repositoryPath, participantA, relationshipEntityId)));
        Assert.True(File.Exists(GetRelationshipMarkerPath(this.repositoryPath, participantB, relationshipEntityId)));
    }

    [Fact]
    public async Task UpdateReplace_DeletingRelationshipEntity_DeletesMarkerFilesForParticipants()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var relationshipEntityId = new EntityId(Guid.Parse("fc7177f2-db5a-4215-9ed5-b1c821c4d4b3"));
        var participantA = new EntityId(Guid.Parse("f910764f-97f8-47a6-b30f-278e1a6e66d4"));
        var participantB = new EntityId(Guid.Parse("6daf08cf-f044-4f07-9c9a-a8410bfc9156"));

        var createRelationshipResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(this.CreateRelationshipEntity(relationshipEntityId, participantA, participantB)));
        var relationshipConcurrencyTag = Assert.Single(createRelationshipResult.EntityResults).ConcurrencyTag!.Value;

        await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                new EntityChange
                {
                    EntityId = relationshipEntityId,
                    ConcurrencyTag = relationshipConcurrencyTag,
                    Data = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                }));

        Assert.False(File.Exists(GetRelationshipMarkerPath(this.repositoryPath, participantA, relationshipEntityId)));
        Assert.False(File.Exists(GetRelationshipMarkerPath(this.repositoryPath, participantB, relationshipEntityId)));
    }

    [Fact]
    public async Task RecreateDataAccessLayer_CanReadPreviouslyWrittenData()
    {
        var entityId = new EntityId(Guid.Parse("5feb8fc4-dfca-4cc8-8366-f7f6bf4d3c50"));
        {
            var firstInstance = new FilesystemDataAccessLayer(this.repositoryPath);
            var createResult = await firstInstance.UpdateAsync(CreateUpdateRequest(this.CreateEntity(entityId, "persisted")));
            Assert.Equal(UpdateState.Added, Assert.Single(createResult.EntityResults).UpdateState);
        }

        var secondInstance = new FilesystemDataAccessLayer(this.repositoryPath);
        var getResult = await secondInstance.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                    },
                ],
                Timestamps = new Timestamp?[] { null },
            });

        var snapshot = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Contains("\"persisted\"", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.repositoryPath))
        {
            Directory.Delete(this.repositoryPath, true);
        }
    }

    private static UpdateRequest CreateUpdateRequest(
        EntityChange change)
    {
        return new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown
                {
                    Text = "test update",
                },
            },
            Changes = new[] { change },
        };
    }

    private static string GetEntityPath(
        string rootPath,
        EntityId entityId)
    {
        return Path.Combine(FilesystemDataAccessLayer.GetEntityDirectory(rootPath, entityId), $"{entityId.Value:D}.json");
    }

    private static string GetRelationshipMarkerPath(
        string rootPath,
        EntityId participantEntityId,
        EntityId relationshipEntityId)
    {
        return Path.Combine(
            FilesystemDataAccessLayer.GetEntityDirectory(rootPath, participantEntityId),
            $"{participantEntityId.Value:D}_{relationshipEntityId.Value:D}.rel");
    }

    private EntityChange CreateEntity(
        EntityId entityId,
        string name)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"]
            }
            """);

        return new EntityChange
        {
            EntityId = entityId,
            Data = document.RootElement.Clone(),
            EntityChangeMode = EntityChangeMode.Replace,
        };
    }

    private EntityChange CreateRelationshipEntity(
        EntityId relationshipEntityId,
        EntityId participantA,
        EntityId participantB)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipEntityId.Value:D}}",
              "entity-types": ["relationship", "related-to"],
              "names": ["a-to-b"],
              "related-entity-ids": ["{{participantA.Value:D}}", "{{participantB.Value:D}}"],
              "relationship-roles": ["source", "target"]
            }
            """);

        return new EntityChange
        {
            EntityId = relationshipEntityId,
            Data = document.RootElement.Clone(),
            EntityChangeMode = EntityChangeMode.Replace,
        };
    }
}
