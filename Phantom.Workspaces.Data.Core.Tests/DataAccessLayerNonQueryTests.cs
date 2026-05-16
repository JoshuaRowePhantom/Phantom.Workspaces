using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public abstract class DataAccessLayerNonQueryTests
{
    protected static readonly EntityId SampleEntityId = new(
        Guid.Parse("5a48c0ee-4a39-4d1b-9c6c-c3de6e67ce27"));

    protected abstract IDataAccessLayer CreateDataAccessLayer();

    [Fact]
    public async Task Populate_CreateEntity_CanReadBackByIdNameAndTypeName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var createTime = (await this.GetSingleHistoryAsync(dataAccessLayer)).UpdateTimes.Single();

        var byId = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, null);
        Assert.Equal("one", this.GetName(byId.Data));

        var byName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, "one", null);
        Assert.Equal("one", this.GetName(byName.Data));

        var byTypeAndName = await this.GetSingleSnapshotByTypeAndNameAsync(dataAccessLayer, "entity", "one", null);
        Assert.Equal("one", this.GetName(byTypeAndName.Data));

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        Assert.Single(history.UpdateTimes);
        Assert.Equal(createTime, history.UpdateTimes.Single());
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithMatchingConcurrencyTag_ReplacesDataAndAdvancesConcurrencyTag()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var populatedExport = await dataAccessLayer.ExportAsync(new ExportRequest(null));

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var createTime = (await this.GetSingleHistoryAsync(dataAccessLayer)).UpdateTimes.Single();
        var createConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Update entity")),
                new[] { this.CreateUpsertChange("two", createConcurrencyTag) }));

        var updateEntityResult = AssertSuccessfulResult(updateResult, UpdateState.Updated);
        Assert.NotEqual(createConcurrencyTag, updateEntityResult.ConcurrencyTag);
        var updatedConcurrencyTag = updateEntityResult.ConcurrencyTag!.Value;

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, null);
        Assert.Equal("two", this.GetName(latestById.Data));

        var latestByName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, "two", null);
        Assert.Equal("two", this.GetName(latestByName.Data));

        var historicalById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, createTime);
        Assert.Equal("one", this.GetName(historicalById.Data));

        var historicalByName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, "one", createTime);
        Assert.Equal("one", this.GetName(historicalByName.Data));

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        var updateTimes = history.UpdateTimes.ToArray();
        Assert.Equal(2, updateTimes.Length);
        Assert.Equal(createTime, updateTimes[0]);
        Assert.NotEqual(createTime, updateTimes[1]);

        var exportAll = await dataAccessLayer.ExportAsync(new ExportRequest(null));
        Assert.Equal(populatedExport.ChangeBatches.Count + 2, exportAll.ChangeBatches.Count);
        Assert.Equal(updateTimes[1], exportAll.FinalSnapshotTime);

        var exportAfterCreate = await dataAccessLayer.ExportAsync(new ExportRequest(createTime));
        Assert.Single(exportAfterCreate.ChangeBatches);
        Assert.Equal(updateTimes[1], exportAfterCreate.ChangeBatches.Single().ChangeTime);

        var changedAfterCreate = await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest(
                new[]
                {
                    new EntityIdTimestamp(SampleEntityId, createTime),
                }));
        Assert.Single(changedAfterCreate.Entities);

        var changedAfterUpdate = await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest(
                new[]
                {
                    new EntityIdTimestamp(SampleEntityId, updateTimes[1]),
                }));
        Assert.Empty(changedAfterUpdate.Entities);

        Assert.NotEqual(createConcurrencyTag, updatedConcurrencyTag);
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithoutConcurrencyTag_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Update entity without concurrency tag")),
                new[] { this.CreateUpsertChange("two") }));

        var failedResult = Assert.Single(updateResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failedResult.ConcurrencyMatchState);
        Assert.Equal(SampleEntityId, failedResult.RequestedEntityId);
        Assert.Equal(SampleEntityId, failedResult.ResultingEntityId);
        Assert.NotNull(failedResult.CurrentEntity);
        Assert.Equal(SampleEntityId, failedResult.CurrentEntity!.EntityId);
        Assert.Equal(createResult.EntityResults.Single().ConcurrencyTag, failedResult.CurrentEntity.ConcurrencyTag);
        Assert.Equal("one", this.GetName(failedResult.CurrentEntity?.Data));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("Concurrency tag is required.", StringComparison.Ordinal));

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, null);
        Assert.Equal("one", this.GetName(latestById.Data));

        var latestByName = await dataAccessLayer.GetAsync(
            new GetRequest(
                new[]
                {
                    new GetEntityRequest(
                        null,
                        new EntityName("two"),
                        null,
                        null),
                },
                null,
                new Timestamp?[] { null }));
        Assert.Empty(Assert.Single(latestByName.Batches).Entities);

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        Assert.Single(history.UpdateTimes);
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithStaleConcurrencyTag_FailsAndPreservesCurrentValue()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Update entity")),
                new[] { this.CreateUpsertChange("two", initialConcurrencyTag) }));
        var updatedEntityResult = AssertSuccessfulResult(updateResult, UpdateState.Updated);
        var currentConcurrencyTag = updatedEntityResult.ConcurrencyTag!.Value;

        var staleUpdateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Stale update")),
                new[] { this.CreateUpsertChange("three", initialConcurrencyTag) }));

        var failedResult = Assert.Single(staleUpdateResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failedResult.ConcurrencyMatchState);
        Assert.Equal(SampleEntityId, failedResult.RequestedEntityId);
        Assert.Equal(SampleEntityId, failedResult.ResultingEntityId);
        Assert.NotNull(failedResult.CurrentEntity);
        Assert.Equal(SampleEntityId, failedResult.CurrentEntity!.EntityId);
        Assert.Equal(currentConcurrencyTag, failedResult.CurrentEntity.ConcurrencyTag);
        Assert.Equal("two", this.GetName(failedResult.CurrentEntity?.Data));
        Assert.Equal(currentConcurrencyTag, failedResult.ConcurrencyTag);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("Concurrency tag does not match.", StringComparison.Ordinal));

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, null);
        Assert.Equal("two", this.GetName(latestById.Data));

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        Assert.Equal(2, history.UpdateTimes.Count);
    }

    [Fact]
    public async Task Populate_DeleteEntity_WithMatchingConcurrencyTag_RemovesEntity()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var populatedExport = await dataAccessLayer.ExportAsync(new ExportRequest(null));

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var createConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var deleteResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Delete entity")),
                new[] { this.CreateDeleteChange(createConcurrencyTag) }));

        var deletedEntityResult = AssertSuccessfulResult(deleteResult, UpdateState.Removed);
        Assert.NotNull(deletedEntityResult.ConcurrencyTag);

        var deletedSnapshot = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, null);
        Assert.Null(deletedSnapshot.Data);

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        Assert.Equal(2, history.UpdateTimes.Count);

        var export = await dataAccessLayer.ExportAsync(new ExportRequest(null));
        Assert.Equal(populatedExport.ChangeBatches.Count + 2, export.ChangeBatches.Count);
    }

    [Fact]
    public async Task Populate_DeleteEntity_WithStaleConcurrencyTag_FailsAndPreservesCurrentValue()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, "one");
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Update entity")),
                new[] { this.CreateUpsertChange("two", initialConcurrencyTag) }));
        var currentConcurrencyTag = AssertSuccessfulResult(updateResult, UpdateState.Updated).ConcurrencyTag!.Value;

        var deleteResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Stale delete")),
                new[] { this.CreateDeleteChange(initialConcurrencyTag) }));

        var failedResult = Assert.Single(deleteResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failedResult.ConcurrencyMatchState);
        Assert.NotNull(failedResult.CurrentEntity);
        Assert.Equal(SampleEntityId, failedResult.CurrentEntity!.EntityId);
        Assert.Equal(currentConcurrencyTag, failedResult.ConcurrencyTag);
        Assert.Equal("two", this.GetName(failedResult.CurrentEntity?.Data));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("Concurrency tag does not match.", StringComparison.Ordinal));

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer, null);
        Assert.Equal("two", this.GetName(latestById.Data));

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        Assert.Equal(2, history.UpdateTimes.Count);
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
        var createAdditionalParticipantResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Create additional participant")),
                new[]
                {
                    new EntityChange(
                        additionalParticipantId,
                        null,
                        additionalParticipantDocument.RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));
        var additionalParticipantEntityResult = Assert.Single(createAdditionalParticipantResult.EntityResults);
        Assert.Equal(UpdateState.Added, additionalParticipantEntityResult.UpdateState);

        using var relationshipDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "8fcb8f49-a3aa-4498-9f3d-4a8e6992dd69",
              "entity-types": ["relationship", "related-to"],
              "names": ["one-related"],
              "related-entity-ids": ["{{SampleEntityId.Value:D}}", "{{additionalParticipantId.Value:D}}"],
              "relationship-roles": ["source", "target"]
            }
            """);
        var relationshipEntityId = new EntityId(Guid.Parse("8fcb8f49-a3aa-4498-9f3d-4a8e6992dd69"));
        var createRelationshipResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Create relationship")),
                new[]
                {
                    new EntityChange(
                        relationshipEntityId,
                        null,
                        relationshipDocument.RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));
        var relationshipCreateEntityResult = Assert.Single(createRelationshipResult.EntityResults);
        Assert.Equal(UpdateState.Added, relationshipCreateEntityResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, relationshipCreateEntityResult.ConcurrencyMatchState);
        Assert.Equal(relationshipEntityId, relationshipCreateEntityResult.RequestedEntityId);
        Assert.Equal(relationshipEntityId, relationshipCreateEntityResult.ResultingEntityId);
        Assert.Empty(relationshipCreateEntityResult.Errors);

        var noRelationships = await dataAccessLayer.GetAsync(
            new GetRequest(
                new[] { new GetEntityRequest(SampleEntityId, null, null, null) },
                null,
                new Timestamp?[] { null }));
        Assert.Empty(Assert.Single(Assert.Single(noRelationships.Batches).Entities).Relationships);

        var allRelationships = await dataAccessLayer.GetAsync(
            new GetRequest(
                new[] { new GetEntityRequest(SampleEntityId, null, null, null) },
                Array.Empty<GetRelationshipRequest>(),
                new Timestamp?[] { null }));
        var allRelationshipsSnapshot = Assert.Single(Assert.Single(allRelationships.Batches).Entities);
        var relationship = Assert.Single(allRelationshipsSnapshot.Relationships);
        Assert.Equal(relationshipEntityId, relationship.EntityId);
        Assert.NotNull(relationship.Data);
        Assert.Equal("one-related", this.GetName(relationship.Data));

        var filteredOutByEntityRequest = await dataAccessLayer.GetAsync(
            new GetRequest(
                new[]
                {
                    new GetEntityRequest(
                        SampleEntityId,
                        null,
                        null,
                        new[]
                        {
                            new GetRelationshipRequest(
                                new RelationshipTypeNames(new[] { "unrelated-type" }),
                                null),
                        }),
                },
                Array.Empty<GetRelationshipRequest>(),
                new Timestamp?[] { null }));
        Assert.Empty(Assert.Single(Assert.Single(filteredOutByEntityRequest.Batches).Entities).Relationships);

        var filteredByTypeAndRole = await dataAccessLayer.GetAsync(
            new GetRequest(
                new[] { new GetEntityRequest(SampleEntityId, null, null, null) },
                new[]
                {
                    new GetRelationshipRequest(
                        new RelationshipTypeNames(new[] { "related-to" }),
                        new RoleNames(new[] { "source" })),
                },
                new Timestamp?[] { null }));
        Assert.Single(Assert.Single(Assert.Single(filteredByTypeAndRole.Batches).Entities).Relationships);
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
        return new EntityChange(
            SampleEntityId,
            concurrencyTag,
            this.CreateEntity(name),
            EntityChangeMode.Replace);
    }

    protected EntityChange CreateDeleteChange(
        ConcurrencyTag concurrencyTag)
    {
        return new EntityChange(
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
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Create entity")),
                new[] { this.CreateUpsertChange(name, concurrencyTag) }));
    }

    private static EntityUpdateResult AssertSuccessfulResult(
        UpdateResult result,
        UpdateState expectedState)
    {
        var entityResult = Assert.Single(result.EntityResults);
        Assert.Equal(expectedState, entityResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState);
        Assert.Equal(SampleEntityId, entityResult.RequestedEntityId);
        Assert.Equal(SampleEntityId, entityResult.ResultingEntityId);
        Assert.Empty(entityResult.Errors);
        Assert.NotNull(entityResult.ConcurrencyTag);
        return entityResult;
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByIdAsync(
        IDataAccessLayer dataAccessLayer,
        Timestamp? timestamp)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    new GetEntityRequest(
                        SampleEntityId,
                        null,
                        null,
                        null),
                    timestamp)));
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByNameAsync(
        IDataAccessLayer dataAccessLayer,
        string name,
        Timestamp? timestamp)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    new GetEntityRequest(
                        null,
                        new EntityName(name),
                        null,
                        null),
                    timestamp)));
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByTypeAndNameAsync(
        IDataAccessLayer dataAccessLayer,
        string typeName,
        string name,
        Timestamp? timestamp)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    new GetEntityRequest(
                        null,
                        new EntityName(name),
                        new EntityTypeNames(new[] { typeName }),
                        null),
                    timestamp)));
    }

    private static GetRequest CreateGetRequest(
        GetEntityRequest entity,
        Timestamp? timestamp)
    {
        return new GetRequest(
            new[] { entity },
            null,
            new Timestamp?[] { timestamp });
    }

    private EntitySnapshot GetSingleSnapshotAsync(
        GetResult result)
    {
        return Assert.Single(Assert.Single(result.Batches).Entities);
    }

    private async Task<EntityHistoryEntry> GetSingleHistoryAsync(
        IDataAccessLayer dataAccessLayer)
    {
        return Assert.Single(
            (await dataAccessLayer.GetHistoryAsync(
                new GetHistoryRequest(new[] { SampleEntityId })))
            .History);
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
}
