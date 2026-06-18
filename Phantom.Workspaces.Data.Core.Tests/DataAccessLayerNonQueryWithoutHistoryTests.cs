using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public abstract class DataAccessLayerNonQueryWithoutHistoryTests
{
    protected static readonly EntityId SampleEntityId = new EntityId();

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

        var entityName = new EntityName("one");
        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var byId = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal(entityName, this.GetName(byId.Data));

        var byName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, entityName);
        Assert.Equal(entityName, this.GetName(byName.Data));

        var byTypeAndName = await this.GetSingleSnapshotByTypeAndNameAsync(
            dataAccessLayer,
            new EntityTypeNameSet(new[] { "entity" }),
            entityName);
        Assert.Equal(entityName, this.GetName(byTypeAndName.Data));
    }

    [Fact]
    public async Task Populate_CreateEntity_WithTwoComponentName_CanReadBackByName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("parent", "child"));
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var byName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, new EntityName("parent", "child"));
        Assert.Equal(SampleEntityId, byName.EntityId);
    }

    [Fact]
    public async Task Populate_GetById_WithPropertiesFilter_IsProcessed()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("properties", "filter"));
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var result = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityId = SampleEntityId,
                    Properties = ["display-name"],
                },
                null,
                properties: ["display-name"]));

        var entity = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(SampleEntityId, entity.EntityId);
    }

    [Fact]
    public async Task Populate_GetByName_EnumerateSelf_ReturnsOnlySelf()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var rootEntityId = new EntityId("49bf663e-4c2b-4287-9cac-ea146a7524d6");
        var childEntityId = new EntityId("006a3225-0d15-41bd-935d-9fc8d5f9515d");
        var grandchildEntityId = new EntityId("6b70aef9-1824-486f-84f8-47f5579ca40a");
        var rootName = new EntityName("tree", "root");
        var childName = new EntityName("tree", "root", "child");
        var grandchildName = new EntityName("tree", "root", "child", "leaf");
        await this.CreateHierarchyAsync(dataAccessLayer, rootEntityId, rootName, childEntityId, childName, grandchildEntityId, grandchildName);

        var result = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = rootName,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                },
                null));

        var entities = Assert.Single(result.Batches).Entities;
        var entityIds = entities.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(rootEntityId, entityIds);
        Assert.DoesNotContain(childEntityId, entityIds);
        Assert.DoesNotContain(grandchildEntityId, entityIds);
    }

    [Fact]
    public async Task Populate_GetByName_EnumerateChildren_ReturnsOnlyDirectChildren()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var rootEntityId = new EntityId("b95ca95f-82dc-4eb4-b463-649737e5b9af");
        var childEntityId = new EntityId("e6eccce4-149d-4af8-8a4a-8d4d935d141f");
        var grandchildEntityId = new EntityId("8a0d5f6e-7fe7-49c7-bc1f-44eb3d4c2990");
        var siblingRootEntityId = new EntityId("7247bc9b-f347-4c85-a0ad-f9a77370e247");
        var rootName = new EntityName("tree", "root");
        var childName = new EntityName("tree", "root", "child");
        var grandchildName = new EntityName("tree", "root", "child", "leaf");
        await this.CreateHierarchyAsync(dataAccessLayer, rootEntityId, rootName, childEntityId, childName, grandchildEntityId, grandchildName);
        await this.CreateNamedEntityAsync(dataAccessLayer, siblingRootEntityId, new EntityName("tree", "other", "child"));

        var result = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = rootName,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                },
                null));

        var entities = Assert.Single(result.Batches).Entities;
        var entityIds = entities.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(childEntityId, entityIds);
        Assert.DoesNotContain(rootEntityId, entityIds);
        Assert.DoesNotContain(grandchildEntityId, entityIds);
        Assert.DoesNotContain(siblingRootEntityId, entityIds);
    }

    [Fact]
    public async Task Populate_GetByName_EnumerateAllChildren_ReturnsDescendantsOnly()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var rootEntityId = new EntityId("6a8e14dd-c6fd-4af2-a824-aa0f7b4ce2c6");
        var childEntityId = new EntityId("8de49f6a-4f6f-42f0-ae4b-1717c3e87071");
        var grandchildEntityId = new EntityId("fb4ea780-2036-4606-8fb5-3f757374f5f4");
        var unrelatedEntityId = new EntityId("9fceec8f-86bb-4348-8465-ed59973aaaf4");
        var rootName = new EntityName("tree", "root");
        var childName = new EntityName("tree", "root", "child");
        var grandchildName = new EntityName("tree", "root", "child", "leaf");
        await this.CreateHierarchyAsync(dataAccessLayer, rootEntityId, rootName, childEntityId, childName, grandchildEntityId, grandchildName);
        await this.CreateNamedEntityAsync(dataAccessLayer, unrelatedEntityId, new EntityName("tree", "unrelated", "leaf"));

        var result = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = rootName,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateAllChildren,
                },
                null));

        var entities = Assert.Single(result.Batches).Entities;
        var entityIds = entities.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(childEntityId, entityIds);
        Assert.Contains(grandchildEntityId, entityIds);
        Assert.DoesNotContain(rootEntityId, entityIds);
        Assert.DoesNotContain(unrelatedEntityId, entityIds);
    }

    [Fact]
    public async Task Populate_GetByName_WithEnumerateChildren_AndSpecifiedTypeSet_ReturnsOnlyMatchingTypes()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var rootName = new EntityName("typed-tree", "root");
        var entityChildId = new EntityId("6020c61d-4090-46ea-bf77-5f5054bc7847");
        var viewChildId = new EntityId("699abf89-4f9c-4333-b58a-8a6fbaf31ab5");
        await this.CreateNamedEntityAsync(dataAccessLayer, new EntityId("973e1989-b515-424f-b73a-1a9214349f7f"), rootName);
        await this.CreateNamedEntityAsync(dataAccessLayer, entityChildId, new EntityName("typed-tree", "root", "entity-child"));
        await this.CreateViewEntityAsync(dataAccessLayer, viewChildId, new EntityName("typed-tree", "root", "view-child"));

        var result = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = rootName,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    EntityTypeNames = new EntityTypeNameSet(["view"]),
                },
                null));

        var entities = Assert.Single(result.Batches).Entities;
        var entityIds = entities.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(viewChildId, entityIds);
        Assert.DoesNotContain(entityChildId, entityIds);
    }

    [Fact]
    public async Task Populate_GetByName_WithEnumerateChildren_AndNullOrEmptyTypeSet_ReturnsAllMatchingNameEntities()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var rootName = new EntityName("typed-tree", "root");
        var entityChildId = new EntityId("8db9dbf6-9641-45d8-8f35-ea6e65fae718");
        var viewChildId = new EntityId("d71150e3-ef89-4309-bf1d-f13645f50521");
        await this.CreateNamedEntityAsync(dataAccessLayer, new EntityId("f8ba2899-0e51-4e3d-8f8d-da590b5447cd"), rootName);
        await this.CreateNamedEntityAsync(dataAccessLayer, entityChildId, new EntityName("typed-tree", "root", "entity-child"));
        await this.CreateViewEntityAsync(dataAccessLayer, viewChildId, new EntityName("typed-tree", "root", "view-child"));

        var withNullTypes = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = rootName,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    EntityTypeNames = null,
                },
                null));
        var nullTypeIds = Assert.Single(withNullTypes.Batches).Entities.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(entityChildId, nullTypeIds);
        Assert.Contains(viewChildId, nullTypeIds);

        var withEmptyTypes = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = rootName,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    EntityTypeNames = new EntityTypeNameSet(Array.Empty<string>()),
                },
                null));
        var emptyTypeIds = Assert.Single(withEmptyTypes.Batches).Entities.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(entityChildId, emptyTypeIds);
        Assert.Contains(viewChildId, emptyTypeIds);
    }

    [Fact]
    public async Task Populate_GetByTypeName_WithoutEntityName_ReturnsMatchingTypes()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var entityId = new EntityId("bc8633d9-8d46-4c18-b310-d2e71e4a24f2");
        var viewEntityId = new EntityId("ad2fcf4e-9501-4638-82cc-a75e6f194d76");
        await this.CreateNamedEntityAsync(dataAccessLayer, entityId, new EntityName("typed-lookup", "entity"));
        await this.CreateViewEntityAsync(dataAccessLayer, viewEntityId, new EntityName("typed-lookup", "view"));

        var result = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityTypeNames = new EntityTypeNameSet(["view"]),
                },
                null));

        var returnedEntityIds = Assert.Single(result.Batches).Entities
            .Select(static entity => entity.EntityId)
            .ToArray();
        Assert.Contains(viewEntityId, returnedEntityIds);
        Assert.DoesNotContain(entityId, returnedEntityIds);
    }

    [Fact]
    public async Task Populate_GetByName_EnumerateChildren_EntityTypesPrefix_ReturnsEntityTypeChildren()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityTypesPrefix = new EntityName("entity-types");

        var selfResult = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = entityTypesPrefix,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                },
                null));
        var selfEntities = Assert.Single(selfResult.Batches).Entities;
        if (selfEntities.Count > 0)
        {
            var parentEntity = Assert.Single(selfEntities);
            Assert.True(this.HasName(parentEntity.Data, entityTypesPrefix));
        }

        var childrenResult = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new GetEntityRequest
                {
                    EntityName = entityTypesPrefix,
                    EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                },
                null));
        var children = Assert.Single(childrenResult.Batches).Entities;
        Assert.NotEmpty(children);
        Assert.All(
            children,
            child => Assert.True(this.HasDirectChildNameUnderPrefix(child.Data, entityTypesPrefix)));
        Assert.Contains(children, child => this.HasName(child.Data, new EntityName("entity-types", "entity")));
        Assert.Contains(children, child => this.HasName(child.Data, new EntityName("entity-types", "view")));
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithMatchingConcurrencyTag_ReplacesDataAndAdvancesConcurrencyTag()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var updatedName = new EntityName("two");
        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var createConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[] { this.CreateUpsertChange(new EntityName("two"), createConcurrencyTag) }));

        var updateEntityResult = AssertSuccessfulResult(updateResult, UpdateState.Updated);
        Assert.NotEqual(createConcurrencyTag, updateEntityResult.ConcurrencyTag);

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal(updatedName, this.GetName(latestById.Data));

        var latestByName = await this.GetSingleSnapshotByNameAsync(dataAccessLayer, updatedName);
        Assert.Equal(updatedName, this.GetName(latestByName.Data));
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithIdenticalContentAndNoConcurrencyTag_IsIgnored()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        var initialResult = AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialTag = initialResult.ConcurrencyTag!.Value;

        var noOpUpdateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("No-op update without concurrency tag"),
                new[] { this.CreateUpsertChange(new EntityName("one")) }));

        var noOpEntityResult = AssertSuccessfulResult(noOpUpdateResult, UpdateState.Updated);
        Assert.Equal(initialTag, noOpEntityResult.ConcurrencyTag);

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal(new EntityName("one"), this.GetName(latestById.Data));
        Assert.Equal(initialTag, latestById.ConcurrencyTag);
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithoutConcurrencyTag_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        AssertSuccessfulResult(createResult, UpdateState.Added);

        var updateResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity without concurrency tag"),
                new[] { this.CreateUpsertChange(new EntityName("two")) }));

        var failedResult = Assert.Single(updateResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failedResult.ConcurrencyMatchState);
        Assert.Equal(SampleEntityId, failedResult.RequestedEntityId);
        Assert.Equal(SampleEntityId, failedResult.ResultingEntityId);
        Assert.NotNull(failedResult.CurrentEntity);
        Assert.Equal(new EntityName("one"), this.GetName(failedResult.CurrentEntity?.Data));
    }

    [Fact]
    public async Task Populate_UpdateEntity_WithStaleConcurrencyTag_FailsAndPreservesCurrentValue()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[] { this.CreateUpsertChange(new EntityName("two"), initialConcurrencyTag) }));
        var currentConcurrencyTag = AssertSuccessfulResult(updateResult, UpdateState.Updated).ConcurrencyTag!.Value;

        var staleUpdateResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Stale update"),
                new[] { this.CreateUpsertChange(new EntityName("three"), initialConcurrencyTag) }));

        var failedResult = Assert.Single(staleUpdateResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(currentConcurrencyTag, failedResult.ConcurrencyTag);

        var latestById = await this.GetSingleSnapshotByIdAsync(dataAccessLayer);
        Assert.Equal(new EntityName("two"), this.GetName(latestById.Data));
    }

    [Fact]
    public async Task Populate_DeleteEntity_WithMatchingConcurrencyTag_RemovesEntity()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
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

        var createResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        AssertSuccessfulResult(createResult, UpdateState.Added);
        var initialConcurrencyTag = createResult.EntityResults.Single().ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[] { this.CreateUpsertChange(new EntityName("two"), initialConcurrencyTag) }));
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
        Assert.Equal(new EntityName("two"), this.GetName(latestById.Data));
    }

    [Fact]
    public async Task Populate_GetEntityRelationships_RespectsRequestAndEntityRelationshipFilters()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var createEntityResult = await this.CreateEntityAsync(dataAccessLayer, new EntityName("one"));
        AssertSuccessfulResult(createEntityResult, UpdateState.Added);
        var additionalParticipantId = new EntityId("5ab56174-f4b0-4f64-bbf7-c96bc5cfe419");
        using var additionalParticipantDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{additionalParticipantId}}",
              "entity-types": ["entity"],
              "names": [["two"]]
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
              "names": [["relationship", "8fcb8f49-a3aa-4498-9f3d-4a8e6992dd69"]],
              "participants": {
                "entities": ["{{SampleEntityId}}", "{{additionalParticipantId}}"]
              }
            }
            """);
        var relationshipEntityId = new EntityId("8fcb8f49-a3aa-4498-9f3d-4a8e6992dd69");
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
                                new RelationshipTypeNameSet(new[] { "unrelated-type" }),
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
        EntityName entityName,
        ConcurrencyTag? concurrencyTag = null)
    {
        return CreateEntityChange(
            SampleEntityId,
            concurrencyTag,
            this.CreateEntity(entityName),
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
        EntityName entityName)
    {
        var serializedEntityName = JsonSerializer.Serialize(new[] { entityName.Components });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{SampleEntityId}}",
              "entity-types": ["entity"],
              "names": {{serializedEntityName}}
            }
            """);

        return document.RootElement.Clone();
    }

    private async Task<UpdateResult> CreateEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName,
        ConcurrencyTag? concurrencyTag = null)
    {
        return await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[] { this.CreateUpsertChange(entityName, concurrencyTag) }));
    }


    private static EntityUpdateResult AssertSuccessfulResult(
        UpdateResult result,
        UpdateState expectedState)
    {
        var entityResult = Assert.Single(result.EntityResults, entityResult => entityResult.RequestedEntityId == SampleEntityId);
        Assert.DoesNotContain(result.EntityResults, static entityResult => entityResult.UpdateState == UpdateState.Failed);
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
        EntityName entityName)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    CreateGetEntityRequest(
                        null,
                        entityName,
                        null,
                        null),
                    null)));
    }

    private async Task<EntitySnapshot> GetSingleSnapshotByTypeAndNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityTypeNameSet entityTypeNames,
        EntityName entityName)
    {
        return this.GetSingleSnapshotAsync(
            await dataAccessLayer.GetAsync(
                CreateGetRequest(
                    CreateGetEntityRequest(
                        null,
                        entityName,
                        entityTypeNames,
                        null),
                    null)));
    }

    private static GetRequest CreateGetRequest(
        IReadOnlyCollection<GetEntityRequest> entities,
        IReadOnlyCollection<GetRelationshipRequest>? relationshipsToReturn,
        IReadOnlyCollection<Timestamp?>? timestamps,
        IReadOnlyCollection<string>? properties = null)
    {
        return new GetRequest
        {
            Entities = entities,
            RelationshipsToReturn = relationshipsToReturn,
            Timestamps = timestamps,
            Properties = properties,
        };
    }

    private static GetRequest CreateGetRequest(
        GetEntityRequest entity,
        Timestamp? timestamp,
        IReadOnlyCollection<string>? properties = null)
    {
        return new GetRequest
        {
            Entities = new[] { entity },
            RelationshipsToReturn = null,
            Timestamps = new Timestamp?[] { timestamp },
            Properties = properties,
        };
    }

    private EntitySnapshot GetSingleSnapshotAsync(
        GetResult result)
    {
        return Assert.Single(Assert.Single(result.Batches).Entities);
    }

    private bool HasDirectChildNameUnderPrefix(
        JsonElement? data,
        EntityName prefix)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.Value.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var nameElement in namesElement.EnumerateArray())
        {
            var name = nameElement.TryReadEntityName();
            if (name is null)
            {
                continue;
            }

            if (name.Value.Components.Length == prefix.Components.Length + 1
                && name.Value.Components.Take(prefix.Components.Length)
                    .SequenceEqual(prefix.Components, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasName(
        JsonElement? data,
        EntityName expectedName)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!data.Value.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var nameElement in namesElement.EnumerateArray())
        {
            var entityName = nameElement.TryReadEntityName();
            if (entityName is not null
                && entityName.Value.Components.SequenceEqual(expectedName.Components, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private EntityName? GetName(
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
            var entityName = nameElement.TryReadEntityName();
            if (entityName is not null)
            {
                return entityName;
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

    private async Task CreateHierarchyAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId rootEntityId,
        EntityName rootName,
        EntityId childEntityId,
        EntityName childName,
        EntityId grandchildEntityId,
        EntityName grandchildName)
    {
        await this.CreateNamedEntityAsync(dataAccessLayer, rootEntityId, rootName);
        await this.CreateNamedEntityAsync(dataAccessLayer, childEntityId, childName);
        await this.CreateNamedEntityAsync(dataAccessLayer, grandchildEntityId, grandchildName);
    }

    private async Task CreateNamedEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        EntityName entityName)
    {
        var serializedEntityName = entityName.Components.Length == 1
            ? JsonSerializer.Serialize(new[] { entityName.Components[0] })
            : JsonSerializer.Serialize(new object[] { entityName.Components });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": {{serializedEntityName}}
            }
            """);

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create named entity"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        document.RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));
        var entityResult = Assert.Single(result.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Equal(UpdateState.Added, entityResult.UpdateState);
    }

    private async Task CreateViewEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        EntityName entityName)
    {
        var serializedEntityName = entityName.Components.Length == 1
            ? JsonSerializer.Serialize(new[] { entityName.Components[0] })
            : JsonSerializer.Serialize(new object[] { entityName.Components });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["view"],
              "names": {{serializedEntityName}},
              "title": { "default": "Typed view child" },
              "sub-views": []
            }
            """);

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create view entity"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        document.RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));
        var entityResult = Assert.Single(result.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Equal(UpdateState.Added, entityResult.UpdateState);
    }

    private static GetEntityRequest CreateGetEntityRequest(
        EntityId? entityId,
        EntityName? entityName,
        EntityTypeNameSet? entityTypeNames,
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
        RelationshipTypeNameSet? relationshipTypeNames,
        RoleNameSet? relationshipRoleNames)
    {
        return new GetRelationshipRequest
        {
            RelationshipTypeNames = relationshipTypeNames,
            RelationshipRoleNames = relationshipRoleNames,
        };
    }
}
