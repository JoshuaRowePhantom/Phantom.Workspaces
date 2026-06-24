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
    public void ComputeEntityNameHash_SameName_ReturnsSameHash()
    {
        var name = new EntityName("foo", "bar");
        var firstHash = FilesystemDataAccessLayer.ComputeEntityNameHash(name);
        var secondHash = FilesystemDataAccessLayer.ComputeEntityNameHash(name);

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(24, firstHash.Length);
        Assert.Matches("^[0-9a-f]{24}$", firstHash);
    }

    [Fact]
    public void ComputeEntityNameHash_DifferentComponentBoundaries_ReturnDifferentHashes()
    {
        var splitName = new EntityName("foo", "bar");
        var mergedBoundaryName = new EntityName("fo", "obar");

        var splitHash = FilesystemDataAccessLayer.ComputeEntityNameHash(splitName);
        var mergedBoundaryHash = FilesystemDataAccessLayer.ComputeEntityNameHash(mergedBoundaryName);

        Assert.NotEqual(splitHash, mergedBoundaryHash);
    }

    [Fact]
    public async Task UpdateReplace_CreatesEntityJsonFile()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId("d0dc5498-f970-4ce4-97fc-f75284453a15");

        var result = await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(entityId, "one")));
        Assert.Equal(UpdateState.Added, Assert.Single(result.EntityResults).UpdateState);

        Assert.True(File.Exists(GetEntityPath(this.repositoryPath, entityId)));
    }

    [Fact]
    public async Task UpdateReplace_WithNullData_DeletesEntityJsonFile()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId("2ea08302-7e2f-4716-b478-8cba08f6db8c");

        var createResult = await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(entityId, "one")));
        var concurrencyTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var deleteResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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
    public async Task UpdateReplace_CreatesEntityNameAndPrefixIndexFiles()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId("cb4963cb-926f-46b8-8568-f37f1ca95c5b");
        var entityName = new EntityName("foo", "bar", "baz");

        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(entityId, entityName)));

        var fullNameIndexPath = GetEntityNameIndexPath(this.repositoryPath, entityName, entityId);
        Assert.True(File.Exists(fullNameIndexPath));
        Assert.Equal(0L, new FileInfo(fullNameIndexPath).Length);

        var emptyPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, EntityName.Root, entityId);
        var firstPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, new EntityName("foo"), entityId);
        var secondPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, new EntityName("foo", "bar"), entityId);
        var fullPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, entityName, entityId);
        Assert.True(File.Exists(emptyPrefixIndexPath));
        Assert.True(File.Exists(firstPrefixIndexPath));
        Assert.True(File.Exists(secondPrefixIndexPath));
        Assert.True(File.Exists(fullPrefixIndexPath));
        Assert.Equal(0L, new FileInfo(emptyPrefixIndexPath).Length);
        Assert.Equal(0L, new FileInfo(firstPrefixIndexPath).Length);
        Assert.Equal(0L, new FileInfo(secondPrefixIndexPath).Length);
        Assert.Equal(0L, new FileInfo(fullPrefixIndexPath).Length);
    }

    [Fact]
    public async Task UpdateReplace_DeletingEntity_DeletesEntityNameAndPrefixIndexFiles()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId("8b157415-3051-42b9-b976-811e068f4a4a");
        var entityName = new EntityName("to-delete", "prefix");

        var createResult = await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(entityId, entityName)));
        var concurrencyTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var fullNameIndexPath = GetEntityNameIndexPath(this.repositoryPath, entityName, entityId);
        var emptyPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, EntityName.Root, entityId);
        var firstPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, new EntityName("to-delete"), entityId);
        var fullPrefixIndexPath = GetEntityNamePrefixIndexPath(this.repositoryPath, entityName, entityId);
        Assert.True(File.Exists(fullNameIndexPath));
        Assert.True(File.Exists(emptyPrefixIndexPath));
        Assert.True(File.Exists(firstPrefixIndexPath));
        Assert.True(File.Exists(fullPrefixIndexPath));

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = concurrencyTag,
                    Data = null,
                    EntityChangeMode = EntityChangeMode.Replace,
                }));

        Assert.False(File.Exists(fullNameIndexPath));
        Assert.False(File.Exists(emptyPrefixIndexPath));
        Assert.False(File.Exists(firstPrefixIndexPath));
        Assert.False(File.Exists(fullPrefixIndexPath));
    }

    [Fact]
    public async Task Get_ByName_DoesNotFallbackToEntityEnumeration_WhenNameIndexMissing()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var entityId = new EntityId("8b83d2a4-c238-4ea6-b6d1-a78f9f84cf3a");
        var entityName = new EntityName("missing", "index");

        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(entityId, entityName)));
        File.Delete(GetEntityNameIndexPath(this.repositoryPath, entityName, entityId));

        var byNameResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
                Timestamps = new Timestamp?[] { null },
            });
        Assert.Empty(Assert.Single(byNameResult.Batches).Entities);

        var byIdResult = await dataAccessLayer.GetAsync(
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
        Assert.Single(Assert.Single(byIdResult.Batches).Entities);
    }

    [Fact]
    public async Task UpdateReplace_DifferentComponentBoundaries_UseDistinctNameHashes()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var firstEntityId = new EntityId("2f6ecf68-e76e-4fea-bac8-2b3f8e6a9669");
        var secondEntityId = new EntityId("59fc9c73-16d4-44f9-9d49-ae32a36f7f79");
        var firstName = new EntityName("foo", "bar");
        var secondName = new EntityName("fo", "obar");

        var firstHashPath = GetEntityNameIndexPath(this.repositoryPath, firstName, firstEntityId);
        var secondHashPath = GetEntityNameIndexPath(this.repositoryPath, secondName, secondEntityId);
        Assert.NotEqual(firstHashPath, secondHashPath);

        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(firstEntityId, firstName)));
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(this.CreateEntity(secondEntityId, secondName)));
        Assert.True(File.Exists(firstHashPath));
        Assert.True(File.Exists(secondHashPath));

        var firstResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = firstName,
                    },
                ],
                Timestamps = new Timestamp?[] { null },
            });
        Assert.Equal(firstEntityId, Assert.Single(Assert.Single(firstResult.Batches).Entities).EntityId);

        var secondResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = secondName,
                    },
                ],
                Timestamps = new Timestamp?[] { null },
            });
        Assert.Equal(secondEntityId, Assert.Single(Assert.Single(secondResult.Batches).Entities).EntityId);
    }

    [Fact]
    public async Task UpdateReplace_RelationshipEntity_CreatesMarkerFilesForParticipants()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var relationshipEntityId = new EntityId("8c7679a1-9f2d-4f8d-b611-6496c38f3ac5");
        var participantA = new EntityId("f2978104-2f69-4467-bd07-8efd48f8536f");
        var participantB = new EntityId("3a01ed74-b4b8-4b4d-8a40-f32cf3ec2867");

        var createRelationshipResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(this.CreateRelationshipEntity(relationshipEntityId, participantA, participantB)));
        Assert.Equal(UpdateState.Added, Assert.Single(createRelationshipResult.EntityResults).UpdateState);

        Assert.True(File.Exists(GetRelationshipMarkerPath(this.repositoryPath, participantA, relationshipEntityId)));
        Assert.True(File.Exists(GetRelationshipMarkerPath(this.repositoryPath, participantB, relationshipEntityId)));
    }

    [Fact]
    public async Task UpdateReplace_DeletingRelationshipEntity_DeletesMarkerFilesForParticipants()
    {
        var dataAccessLayer = new FilesystemDataAccessLayer(this.repositoryPath);
        var relationshipEntityId = new EntityId("fc7177f2-db5a-4215-9ed5-b1c821c4d4b3");
        var participantA = new EntityId("f910764f-97f8-47a6-b30f-278e1a6e66d4");
        var participantB = new EntityId("6daf08cf-f044-4f07-9c9a-a8410bfc9156");

        var createRelationshipResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(this.CreateRelationshipEntity(relationshipEntityId, participantA, participantB)));
        var relationshipConcurrencyTag = Assert.Single(createRelationshipResult.EntityResults).ConcurrencyTag!.Value;

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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
        var entityId = new EntityId("5feb8fc4-dfca-4cc8-8366-f7f6bf4d3c50");
        {
            var firstInstance = new FilesystemDataAccessLayer(this.repositoryPath);
            var createResult = await RequireUpdateSucceedsAsync(firstInstance, CreateUpdateRequest(this.CreateEntity(entityId, "persisted")));
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
        return Path.Combine(FilesystemDataAccessLayer.GetEntityDirectory(rootPath, entityId), $"{entityId}.json");
    }

    private static string GetRelationshipMarkerPath(
        string rootPath,
        EntityId participantEntityId,
        EntityId relationshipEntityId)
    {
        return Path.Combine(
            FilesystemDataAccessLayer.GetEntityDirectory(rootPath, participantEntityId),
            $"{participantEntityId}_{relationshipEntityId}.rel");
    }

    private static string GetEntityNameIndexPath(
        string rootPath,
        EntityName entityName,
        EntityId entityId)
    {
        var nameHash = FilesystemDataAccessLayer.ComputeEntityNameHash(entityName);
        return Path.Combine(
            rootPath,
            "entityNames",
            nameHash[..2],
            nameHash.Substring(2, 2),
            nameHash.Substring(4, 2),
            $"{nameHash}_{entityId.Value.ToString("N")}");
    }

    private static string GetEntityNamePrefixIndexPath(
        string rootPath,
        EntityName prefixName,
        EntityId entityId)
    {
        var prefixHash = FilesystemDataAccessLayer.ComputeEntityNameHash(prefixName);
        var entityIdText = entityId.Value.ToString("N");
        return Path.Combine(
            rootPath,
            "entityNamePrefixes",
            prefixHash[..2],
            prefixHash.Substring(2, 2),
            prefixHash.Substring(4, 2),
            prefixHash,
            entityIdText[..2],
            entityIdText.Substring(2, 2),
            entityIdText.Substring(4, 2),
            entityIdText);
    }

    private EntityChange CreateEntity(
        EntityId entityId,
        string name)
    {
        return this.CreateEntity(entityId, new EntityName(name));
    }

    private EntityChange CreateEntity(
        EntityId entityId,
        EntityName entityName)
    {
        var namesJson = entityName.Components.Length == 1
            ? $$"""
              ["{{entityName.Components[0]}}"]
              """
            : JsonSerializer.Serialize(new[] { entityName.Components });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": {{namesJson}}
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
              "entity-id": "{{relationshipEntityId}}",
              "entity-types": ["entity", "relationship", "related"],
              "names": [["a-to-b"]],
              "participants": {
                "entities": ["{{participantA}}", "{{participantB}}"]
              }
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
