using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public abstract class DataAccessLayerNonQueryWithoutHistoryTests
{
    protected static readonly EntityId SampleEntityId = new(
        Guid.Parse("5a48c0ee-4a39-4d1b-9c6c-c3de6e67ce27"));

    protected abstract IDataAccessLayer CreateDataAccessLayer();

    protected static async Task<UpdateResult> RequireUpdateSucceedsAsync(
        IDataAccessLayer dataAccessLayer,
        UpdateRequest request)
    {
        var result = await dataAccessLayer.UpdateAsync(request);
        Assert.True(
            result.EntityResults.All(static entityResult => entityResult.UpdateState != UpdateState.Failed),
            UpdateResultDiagnostics.Describe(result));
        return result;
    }

    protected static async Task<UpdateResult> RequireUpdateFailsAsync(
            IDataAccessLayer dataAccessLayer,
            UpdateRequest request)
    {
            var result = await dataAccessLayer.UpdateAsync(request);
            Assert.True(
                result.EntityResults.Any(static entityResult => entityResult.UpdateState == UpdateState.Failed),
                UpdateResultDiagnostics.Describe(result));
            return result;
    }

    [Fact]
    public async Task Populate_CreateEntity_CanReadBackByIdNameAndTypeName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var byId = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal("one", this.GetName(byId.Data));

        var byName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, "one");
        Assert.Equal("one", this.GetName(byName.Data));

        var byTypeAndName = await this.GetSingleSnapshotByTypeAndNameAsync(dataAccessLayer, "entity", "one");
        Assert.Equal("one", this.GetName(byTypeAndName.Data));
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithMatchingConcurrencyTag_ReplacesDataAndAdvancesConcurrencyTag()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var createConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[] { this.CreateUpsertChange("two", createConcurrencyTag) }));

        var updateEntityResult = AssertSuccessfulResult(updateResult, UpdateState.Updated);
        Assert.NotEqual(createConcurrencyTag, updateEntityResult.ConcurrencyTag);

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal("two", this.GetName(latestById.Data));

        var latestByName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, "two");
        Assert.Equal("two", this.GetName(latestByName.Data));
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithoutConcurrencyTag_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var updateResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity without concurrency tag"),
                new[] { this.CreateUpsertChange("two") }));

        var failedResult = Assert.Single(updateResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failedResult.ConcurrencyMatchState);
        Assert.Equal(SampleEntityId, failedResult.RequestedEntityId);
        Assert.Equal(SampleEntityId, failedResult.ResultingEntityId);
        Assert.NotNull(failedResult.CurrentEntity);
        Assert.Equal("one", this.GetName(failedResult.CurrentEntity?.Data));
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithStaleConcurrencyTag_FailsAndPreservesCurrentValue()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[] { this.CreateUpsertChange("two", initialConcurrencyTag) }));
        var currentConcurrencyTag = AssertSuccessfulResult(updateResult, UpdateState.Updated).ConcurrencyTag!.Value;

        var staleUpdateResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Stale update"),
                new[] { this.CreateUpsertChange("three", initialConcurrencyTag) }));

        var failedResult = Assert.Single(staleUpdateResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(currentConcurrencyTag, failedResult.ConcurrencyTag);

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal("two", this.GetName(latestById.Data));
    }

    [Fact]
    public async Task Populate_DeleteEntity_WithMatchingConcurrencyTag_RemovesEntity()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var createConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var deleteResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Delete entity"),
                new[] { this.CreateDeleteChange(createConcurrencyTag) }));

        AssertSuccessfulResult(deleteResult, UpdateState.Removed);

        var deletedSnapshot = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Null(deletedSnapshot.Data);
    }

    [Fact]
    public async Task Populate_DeleteEntity_WithStaleConcurrencyTag_FailsAndPreservesCurrentValue()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[] { this.CreateUpsertChange("two", initialConcurrencyTag) }));
        var currentConcurrencyTag = AssertSuccessfulResult(updateResult, UpdateState.Updated).ConcurrencyTag!.Value;

        var deleteResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Stale delete"),
                new[] { this.CreateDeleteChange(initialConcurrencyTag) }));

        var failedResult = Assert.Single(deleteResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(currentConcurrencyTag, failedResult.ConcurrencyTag);

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal("two", this.GetName(latestById.Data));
    }

    [Fact]
    public async Task Populate_GetEntityRelationships_RespectsRequestAndEntityRelationshipFilters()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var createEntityResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createEntityResult, UpdateState.Added);
        var additionalParticipantId = new EntityId(Guid.Parse("5ab56174-f4b0-4f64-bbf7-c96bc5cfe419"));
        using var additionalParticipantDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{additionalParticipantId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["two"]
            }
            """);
        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create additional participant"),
                new[]
                {
                    CreateEntityChange(
                        additionalParticipantId,
                        null,
                        additionalParticipantDocument.RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        using var relationshipDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "8fcb8f49-a3aa-4498-9f3d-4a8e6992dd69",
              "entity-types": ["relationship", "related"],
              "names": ["one-related"],
              "participants": {
                "entities": ["{{SampleEntityId.Value:D}}", "{{additionalParticipantId.Value:D}}"]
              }
            }
            """);
        var relationshipEntityId = new EntityId(Guid.Parse("8fcb8f49-a3aa-4498-9f3d-4a8e6992dd69"));
        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create relationship"),
                new[]
                {
                    CreateEntityChange(
                        relationshipEntityId,
                        null,
                        relationshipDocument.RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        var noRelationships = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[] { CreateGetEntityRequest(SampleEntityId, null, null, null) },
                null,
                new Timestamp?[] { null }));
        Assert.Empty(Assert.Single(Assert.Single(noRelationships.Batches).Entities).Relationships);

        var allRelationships = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[] { CreateGetEntityRequest(SampleEntityId, null, null, null) },
                Array.Empty<GetRelationshipRequest>(),
                new Timestamp?[] { null }));
        var relationship = Assert.Single(Assert.Single(Assert.Single(allRelationships.Batches).Entities).Relationships);
        Assert.Equal(relationshipEntityId, relationship.EntityId);

        var filteredOutByEntityRequest = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[]
                {
                    CreateGetEntityRequest(
                        SampleEntityId,
                        null,
                        null,
                        new[]
                        {
                            CreateGetRelationshipRequest(
                                new RelationshipTypeNames(new[] { "unrelated-type" }),
                                null),
                        }),
                },
                Array.Empty<GetRelationshipRequest>(),
                new Timestamp?[] { null }));
        Assert.Empty(Assert.Single(Assert.Single(filteredOutByEntityRequest.Batches).Entities).Relationships);
    }

    protected async Task<IDataAccessLayer> CreatePopulatedDataAccessLayerAsync()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var errors = await new SchemaPopulator(dataAccessLayer).Populate();
        Assert.Empty(errors);
        return dataAccessLayer;
    }

    protected EntityChange CreateUpsertChange(
        string name,
        ConcurrencyTag? concurrencyTag = null)
    {
        return CreateEntityChange(
            SampleEntityId,
            concurrencyTag,
            this.CreateEntity(name),
            EntityChangeMode.Replace);
    }

    protected EntityChange CreateDeleteChange(
        ConcurrencyTag concurrencyTag)
    {
        return CreateEntityChange(
            SampleEntityId,
            concurrencyTag,
            null,
            EntityChangeMode.Replace);
    }

    protected JsonElement CreateEntity(
        string name)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{SampleEntityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"]
            }
            """);

        return document.RootElement.Clone();
    }

    private async Task<UpdateResult> CreateEntityAsync(
        IDataAccessLayer dataAccessLayer,
        string name,
        ConcurrencyTag? concurrencyTag = null)
    {
        return await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[] { this.CreateUpsertChange(name, concurrencyTag) }));
    }

    private static EntityUpdateResult AssertSuccessfulResult(
        UpdateResult result,
        UpdateState expectedState)
    {
        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var entityResult = result.EntityResults.Single();
        Assert.True(entityResult.UpdateState == expectedState, UpdateResultDiagnostics.Describe(result));
        Assert.True(entityResult.ConcurrencyMatchState == ConcurrencyMatchState.Matched, UpdateResultDiagnostics.Describe(result));
        Assert.True(entityResult.RequestedEntityId == SampleEntityId, UpdateResultDiagnostics.Describe(result));
        Assert.True(entityResult.ResultingEntityId == SampleEntityId, UpdateResultDiagnostics.Describe(result));
        Assert.True(entityResult.Errors.Count == 0, UpdateResultDiagnostics.Describe(result));
        Assert.NotNull(entityResult.ConcurrencyTag);
        return entityResult;
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByIdAsync(
        IDataAccessLayer dataAccessLayer)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    CreateGetEntityRequest(
                        SampleEntityId,
                        null,
                        null,
                        null),
                    null)));
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByNameAsync(
        IDataAccessLayer dataAccessLayer,
        string name)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    CreateGetEntityRequest(
                        null,
                        new EntityName(name),
                        null,
                        null),
                    null)));
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByTypeAndNameAsync(
        IDataAccessLayer dataAccessLayer,
        string typeName,
        string name)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    CreateGetEntityRequest(
                        null,
                        new EntityName(name),
                        new EntityTypeNames(new[] { typeName }),
                        null),
                    null)));
    }

    private static GetRequest CreateGetRequest(
        IReadOnlyCollection<GetEntityRequest> entities,
        IReadOnlyCollection<GetRelationshipRequest>? relationshipsToReturn,
        IReadOnlyCollection<Timestamp?>? timestamps)
    {
        return new GetRequest
        {
            Entities = entities,
            RelationshipsToReturn = relationshipsToReturn,
            Timestamps = timestamps,
        };
    }

    private static GetRequest CreateGetRequest(
        GetEntityRequest entity,
        Timestamp? timestamp)
    {
        return new GetRequest
        {
            Entities = new[] { entity },
            RelationshipsToReturn = null,
            Timestamps = new Timestamp?[] { timestamp },
        };
    }

    private EntitySnapshot GetSingleSnapshotAsync(
        GetResult result)
    {
        return Assert.Single(Assert.Single(result.Batches).Entities);
    }

    private string? GetName(
        JsonElement? data)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!data.Value.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var nameElement in namesElement.EnumerateArray())
        {
            if (nameElement.ValueKind == JsonValueKind.String)
            {
                return nameElement.GetString();
            }
        }

        return null;
    }

    private static UpdateRequest CreateUpdateRequest(
        UpdateMetadata updateMetadata,
        IReadOnlyCollection<EntityChange> changes)
    {
        return new UpdateRequest
        {
            UpdateMetadata = updateMetadata,
            Changes = changes,
        };
    }

    private static UpdateMetadata CreateUpdateMetadata(
        string text)
    {
        return new UpdateMetadata
        {
            Comment = new Markdown
            {
                Text = text,
            },
        };
    }

    private static EntityChange CreateEntityChange(
        EntityId? entityId,
        ConcurrencyTag? concurrencyTag,
        JsonElement? data,
        EntityChangeMode entityChangeMode)
    {
        return new EntityChange
        {
            EntityId = entityId,
            ConcurrencyTag = concurrencyTag,
            Data = data,
            EntityChangeMode = entityChangeMode,
        };
    }

    private static GetEntityRequest CreateGetEntityRequest(
        EntityId? entityId,
        EntityName? entityName,
        EntityTypeNames? entityTypeNames,
        IReadOnlyCollection<GetRelationshipRequest>? relationshipsToReturn)
    {
        return new GetEntityRequest
        {
            EntityId = entityId,
            EntityName = entityName,
            EntityTypeNames = entityTypeNames,
            RelationshipsToReturn = relationshipsToReturn,
        };
    }

    private static GetRelationshipRequest CreateGetRelationshipRequest(
        RelationshipTypeNames? relationshipTypeNames,
        RoleNames? relationshipRoleNames)
    {
        return new GetRelationshipRequest
        {
            RelationshipTypeNames = relationshipTypeNames,
            RelationshipRoleNames = relationshipRoleNames,
        };
    }
}
