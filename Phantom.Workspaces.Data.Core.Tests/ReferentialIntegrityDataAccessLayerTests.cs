using System.Text.Json;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.Tests;

public sealed class ReferentialIntegrityDataAccessLayerTests : DataAccessLayerNonQueryTests
{
    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new ReferentialIntegrityDataAccessLayer(new InMemoryDataAccessLayer());
    }

    [Fact]
    public async Task DeleteParticipant_RemovesRelationshipEntityAutomatically()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var sourceEntityId = new EntityId(Guid.Parse("2ff0a31d-a5a6-4202-bcce-a9ca313f2fa9"));
        var destinationEntityId = new EntityId(Guid.Parse("da1fc548-8dac-484f-b9bc-f53d0483f17c"));
        var relationshipEntityId = new EntityId(Guid.Parse("f460aa7e-a13e-4967-a7e2-87fa072f8d95"));

        await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create participant entities"),
                new[]
                {
                    this.CreateEntityChange(sourceEntityId, "source"),
                    this.CreateEntityChange(destinationEntityId, "destination"),
                }));

        var relationshipCreateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create relationship"),
                new[]
                {
                    this.CreateRelationshipChange(
                        relationshipEntityId,
                        sourceEntityId,
                        destinationEntityId),
                }));
        Assert.Equal(UpdateState.Added, Assert.Single(relationshipCreateResult.EntityResults).UpdateState);

        var sourceDeleteTag = (await this.GetEntitySnapshotByIdAsync(dataAccessLayer, sourceEntityId)).ConcurrencyTag;
        var deleteResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Delete source entity"),
                new[]
                {
                    CreateEntityChange(
                        sourceEntityId,
                        sourceDeleteTag,
                        null,
                        EntityChangeMode.Replace),
                }));
        Assert.DoesNotContain(deleteResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        var relationshipSnapshot = await this.GetEntitySnapshotByIdAsync(dataAccessLayer, relationshipEntityId);
        Assert.Null(relationshipSnapshot.Data);
    }

    [Fact]
    public async Task TypedEntityReferences_RequireExistingEntityAndMatchingType()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId(Guid.Parse("1f4ddf45-24f9-460d-9f58-02e3f3f1b278"));
        var schemaName = "https://schemas.phantom.app/workspaces/tests/typed-reference.json";
        var sourceEntityId = new EntityId(Guid.Parse("1a51d9a5-72b2-4427-8cd0-1c238b9bb42a"));
        var wrongTypeTargetEntityId = new EntityId(Guid.Parse("6fb49878-7905-4fb8-9032-ec9fd53d6b84"));
        var missingTargetEntityId = new EntityId(Guid.Parse("c33014c9-66b6-4955-b0aa-f4d5ddfd0e76"));

        await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create typed-ref schema"),
                new[]
                {
                    this.CreateTypedReferenceSchemaChange(schemaEntityId, schemaName),
                }));

        var missingTargetResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with missing target"),
                new[]
                {
                    this.CreateTypedReferenceEntityChange(sourceEntityId, "source-missing", schemaName, missingTargetEntityId),
                }));
        var missingFailure = missingTargetResult.EntityResults.Single(result => result.RequestedEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Failed, missingFailure.UpdateState);
        Assert.Contains(missingFailure.Errors, static error => error.Message.Contains("does not exist", StringComparison.Ordinal));

        var wrongTypeResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with wrong-type target"),
                new[]
                {
                    this.CreateTypedReferenceEntityChange(sourceEntityId, "source-wrong-type", schemaName, wrongTypeTargetEntityId),
                }));
        var wrongTypeFailure = wrongTypeResult.EntityResults.Single(result => result.RequestedEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Failed, wrongTypeFailure.UpdateState);
        Assert.Contains(wrongTypeFailure.Errors, static error => error.Message.Contains("does not match required types", StringComparison.Ordinal));

        var validTargetEntityId = new EntityId(Guid.Parse("91e69f56-e57c-4da8-8450-24f40531f730"));
        var validTargetCreateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create valid target"),
                new[]
                {
                    this.CreateEntityChange(validTargetEntityId, "valid-target"),
                }));
        Assert.True(
            validTargetCreateResult.EntityResults.All(result => result.UpdateState != UpdateState.Failed),
            string.Join(
                Environment.NewLine,
                validTargetCreateResult.EntityResults.SelectMany(entityResult => entityResult.Errors.Select(error => error.Message))));

        var validCreateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with valid target"),
                new[]
                {
                    this.CreateTypedReferenceEntityChange(sourceEntityId, "source-valid", schemaName, validTargetEntityId),
                }));
        Assert.Contains(validCreateResult.EntityResults, static result => result.UpdateState is UpdateState.Added or UpdateState.Updated);
        Assert.DoesNotContain(validCreateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task TypedEntityNameReferences_RequireExistingEntityByName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId(Guid.Parse("fd643f35-56f5-45f7-882f-0f8c81f17f13"));
        var schemaName = "https://schemas.phantom.app/workspaces/tests/typed-reference-by-name.json";
        var sourceEntityId = new EntityId(Guid.Parse("8dc1ff36-74a1-49d1-b11a-0fc0ef1adf2f"));

        await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create typed name-ref schema"),
                new[]
                {
                    this.CreateTypedNameReferenceSchemaChange(schemaEntityId, schemaName),
                }));

        var missingTargetResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create view with missing named reference"),
                new[]
                {
                    this.CreateTypedNameReferenceEntityChange(
                        sourceEntityId,
                        "source-missing-name",
                        schemaName,
                        "missing-target"),
                }));
        var missingFailure = Assert.Single(missingTargetResult.EntityResults);
        Assert.Equal(UpdateState.Failed, missingFailure.UpdateState);
        Assert.Contains(missingFailure.Errors, static error => error.Message.Contains("does not exist", StringComparison.Ordinal));

        var validNamedTargetCreateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create valid named target"),
                new[]
                {
                    this.CreateEntityChange(new EntityId(Guid.Parse("9af2fdce-e592-40a6-9f38-f3c6c9f68aea")), "valid-target"),
                }));
        Assert.True(
            validNamedTargetCreateResult.EntityResults.All(result => result.UpdateState != UpdateState.Failed),
            string.Join(
                Environment.NewLine,
                validNamedTargetCreateResult.EntityResults.SelectMany(entityResult => entityResult.Errors.Select(error => error.Message))));

        var validTargetResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create view with valid named reference"),
                new[]
                {
                    this.CreateTypedNameReferenceEntityChange(
                        sourceEntityId,
                        "source-valid-name",
                        schemaName,
                        "valid-target"),
                }));
        Assert.True(
            validTargetResult.EntityResults.Any(result => result.UpdateState is UpdateState.Added or UpdateState.Updated),
            string.Join(
                Environment.NewLine,
                validTargetResult.EntityResults.SelectMany(entityResult => entityResult.Errors.Select(error => error.Message))));
        Assert.DoesNotContain(validTargetResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task AddEntityAndRelationship_WithMissingReferencedEntities_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId(Guid.Parse("c30108d2-4e17-4e8c-98d6-31ebf54db8de"));
        var relationshipEntityId = new EntityId(Guid.Parse("24c03916-f515-497e-af7b-96e3874eb6ec"));
        var missingTargetEntityId = new EntityId(Guid.Parse("f73dc6ec-432c-46e7-af03-cd53c423f5b3"));
        var existingParticipantEntityId = new EntityId(Guid.Parse("11f245dd-8a17-4f8d-a9f6-4c6ef4f3dadf"));

        await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create existing participant"),
                new[]
                {
                    this.CreateEntityChange(existingParticipantEntityId, "participant"),
                }));

        var addEntityResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity with missing reference"),
                new[]
                {
                    this.CreateEntityWithSingleReferenceChange(
                        entityId,
                        null,
                        "entity-with-missing-reference",
                        missingTargetEntityId),
                }));
        var addEntityFailure = addEntityResult.EntityResults.Single(result => result.RequestedEntityId == entityId);
        Assert.Equal(UpdateState.Failed, addEntityFailure.UpdateState);

        var addRelationshipResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add relationship with missing participant"),
                new[]
                {
                    this.CreateRelationshipChange(
                        relationshipEntityId,
                        existingParticipantEntityId,
                        missingTargetEntityId),
                }));
        var addRelationshipFailure = addRelationshipResult.EntityResults.Single(result => result.RequestedEntityId == relationshipEntityId);
        Assert.Equal(UpdateState.Failed, addRelationshipFailure.UpdateState);
    }

    [Fact]
    public async Task UpdateEntityAndRelationship_WithMissingReferencedEntities_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var sourceEntityId = new EntityId(Guid.Parse("d1594aac-208f-4eb6-a88e-4897be2d8cb6"));
        var targetEntityId = new EntityId(Guid.Parse("4fb7bd08-cc2a-4178-b7bf-cdca9f70c1df"));
        var otherParticipantEntityId = new EntityId(Guid.Parse("331f56a2-f15e-4cd3-9740-29df9f274516"));
        var relationshipEntityId = new EntityId(Guid.Parse("74f9f094-5bea-4108-ae85-e6321a349adb"));
        var missingTargetEntityId = new EntityId(Guid.Parse("48c2e6d8-c1cc-4ae8-a846-2127d0a8ecdf"));

        var createResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create baseline entities"),
                new[]
                {
                    this.CreateEntityWithSingleReferenceChange(sourceEntityId, null, "source", targetEntityId),
                    this.CreateEntityChange(targetEntityId, "target"),
                    this.CreateEntityChange(otherParticipantEntityId, "other"),
                    this.CreateRelationshipChange(relationshipEntityId, sourceEntityId, otherParticipantEntityId),
                }));
        Assert.DoesNotContain(createResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        var sourceConcurrencyTag = createResult.EntityResults.Single(result => result.ResultingEntityId == sourceEntityId).ConcurrencyTag!.Value;
        var relationshipConcurrencyTag = createResult.EntityResults.Single(result => result.ResultingEntityId == relationshipEntityId).ConcurrencyTag!.Value;

        var updateSourceResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity to missing reference"),
                new[]
                {
                    this.CreateEntityWithSingleReferenceChange(
                        sourceEntityId,
                        sourceConcurrencyTag,
                        "source",
                        missingTargetEntityId),
                }));
        var updateSourceFailure = updateSourceResult.EntityResults.Single(result => result.RequestedEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Failed, updateSourceFailure.UpdateState);

        var updateRelationshipResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update relationship to missing participant"),
                new[]
                {
                    this.CreateRelationshipChange(
                        relationshipEntityId,
                        sourceEntityId,
                        missingTargetEntityId,
                        relationshipConcurrencyTag),
                }));
        var updateRelationshipFailure = updateRelationshipResult.EntityResults.Single(result => result.RequestedEntityId == relationshipEntityId);
        Assert.Equal(UpdateState.Failed, updateRelationshipFailure.UpdateState);
    }

    [Fact]
    public async Task ReferenceRelationship_IsRemovedOnlyWhenAllReferencesAreGone()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var sourceEntityId = new EntityId(Guid.Parse("b82d6a9c-157f-4f2f-a2cd-3f96035e3585"));
        var targetEntityId = new EntityId(Guid.Parse("60a52352-f9fc-4d15-9ca4-a59e4dbafe1f"));

        var targetCreateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create target"),
                new[]
                {
                    this.CreateEntityChange(targetEntityId, "target"),
                }));
        Assert.Equal(UpdateState.Added, Assert.Single(targetCreateResult.EntityResults).UpdateState);

        var sourceCreateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create source with duplicate references"),
                new[]
                {
                    this.CreateEntityWithMultipleReferencesChange(
                        sourceEntityId,
                        "source",
                        firstReferenceEntityId: targetEntityId,
                        secondReferenceEntityId: targetEntityId),
                }));
        var sourceCreateEntityResult = sourceCreateResult.EntityResults.Single(result => result.ResultingEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Added, sourceCreateEntityResult.UpdateState);

        var relationshipsAfterCreate = await this.GetReferenceRelationshipsAsync(dataAccessLayer, sourceEntityId);
        Assert.Single(relationshipsAfterCreate);

        var removeOneReferenceResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Remove one reference"),
                new[]
                {
                    this.CreateEntityWithSingleReferenceChange(
                        sourceEntityId,
                        sourceCreateEntityResult.ConcurrencyTag!.Value,
                        "source",
                        targetEntityId),
                }));
        var removeOneReferenceEntityResult = removeOneReferenceResult.EntityResults.Single(result => result.ResultingEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Updated, removeOneReferenceEntityResult.UpdateState);

        var relationshipsAfterOneRemoval = await this.GetReferenceRelationshipsAsync(dataAccessLayer, sourceEntityId);
        Assert.Single(relationshipsAfterOneRemoval);

        var removeAllReferencesResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Remove all references"),
                new[]
                {
                    this.CreateEntityWithoutReferencesChange(
                        sourceEntityId,
                        removeOneReferenceEntityResult.ConcurrencyTag!.Value,
                        "source"),
                }));
        Assert.Contains(removeAllReferencesResult.EntityResults, result => result.ResultingEntityId == sourceEntityId && result.UpdateState == UpdateState.Updated);

        var relationshipsAfterAllRemoval = await this.GetReferenceRelationshipsAsync(dataAccessLayer, sourceEntityId);
        Assert.Empty(relationshipsAfterAllRemoval);
    }

    private EntityChange CreateEntityChange(
        EntityId entityId,
        string name,
        string entityType = "entity")
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["{{entityType}}"],
              "names": ["{{name}}"]
            }
            """);
        return CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateRelationshipChange(
        EntityId relationshipEntityId,
        EntityId sourceEntityId,
        EntityId destinationEntityId,
        ConcurrencyTag? concurrencyTag = null)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipEntityId.Value:D}}",
              "entity-types": ["relationship", "related-to"],
              "names": ["source-to-destination"],
              "related-entity-ids": ["{{sourceEntityId.Value:D}}", "{{destinationEntityId.Value:D}}"],
              "relationship-roles": ["source", "destination"]
            }
            """);
        return CreateEntityChange(relationshipEntityId, concurrencyTag, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateTypedReferenceSchemaChange(
        EntityId schemaEntityId,
        string schemaName)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "{{schemaName}}",
              "entity-id": "{{schemaEntityId.Value:D}}",
              "entity-types": ["json-schema"],
              "names": ["{{schemaName}}"],
              "type": "object",
              "properties": {
                "entity-id": {
                  "$ref": "https://schemas.phantom.app/workspaces/data/core/core.json#/$defs/entity-id"
                },
                "entity-types": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "names": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "target-entity-id": {
                  "$ref": "https://schemas.phantom.app/workspaces/data/core/core.json#/$defs/entity-id",
                  "x-entity-type": "entity"
                }
              }
            }
            """);
        return CreateEntityChange(schemaEntityId, null, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateTypedReferenceEntityChange(
        EntityId entityId,
        string name,
        string schemaName,
        EntityId targetEntityId)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "$schema": "{{schemaName}}",
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"],
              "target-entity-id": "{{targetEntityId.Value:D}}"
            }
            """);
        return CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateTypedNameReferenceSchemaChange(
        EntityId entityId,
        string schemaName)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "{{schemaName}}",
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["json-schema"],
              "names": ["{{schemaName}}"],
              "type": "object",
              "properties": {
                "entity-id": {
                  "$ref": "https://schemas.phantom.app/workspaces/data/core/core.json#/$defs/entity-id"
                },
                "entity-types": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "names": {
                  "type": "array",
                  "items": {
                    "$ref": "https://schemas.phantom.app/workspaces/data/core/core.json#/$defs/entity-name"
                  }
                },
                "target-entity-name": {
                  "$ref": "https://schemas.phantom.app/workspaces/data/core/core.json#/$defs/entity-reference",
                  "x-entity-type": "entity"
                }
              }
            }
            """);
        return CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateTypedNameReferenceEntityChange(
        EntityId entityId,
        string name,
        string schemaName,
        string targetName)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "$schema": "{{schemaName}}",
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"],
              "target-entity-name": "{{targetName}}"
            }
            """);
        return CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateEntityWithMultipleReferencesChange(
        EntityId entityId,
        string name,
        EntityId firstReferenceEntityId,
        EntityId secondReferenceEntityId)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"],
              "first-entity-id": "{{firstReferenceEntityId.Value:D}}",
              "second-entity-id": "{{secondReferenceEntityId.Value:D}}"
            }
            """);
        return CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateEntityWithSingleReferenceChange(
        EntityId entityId,
        ConcurrencyTag? concurrencyTag,
        string name,
        EntityId referenceEntityId)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"],
              "first-entity-id": "{{referenceEntityId.Value:D}}"
            }
            """);
        return CreateEntityChange(entityId, concurrencyTag, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private EntityChange CreateEntityWithoutReferencesChange(
        EntityId entityId,
        ConcurrencyTag concurrencyTag,
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
        return CreateEntityChange(entityId, concurrencyTag, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private async Task<EntitySnapshot> GetEntitySnapshotByIdAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId)
    {
        var getResult = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[]
                {
                    CreateGetEntityRequest(entityId, null, null, null),
                },
                null,
                new Timestamp?[] { null }));
        return Assert.Single(Assert.Single(getResult.Batches).Entities);
    }

    private async Task<IReadOnlyCollection<EntitySnapshot>> GetReferenceRelationshipsAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId sourceEntityId)
    {
        var getResult = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[]
                {
                    CreateGetEntityRequest(sourceEntityId, null, null, null),
                },
                new[]
                {
                    CreateGetRelationshipRequest(
                        new RelationshipTypeNames(new[] { "reference" }),
                        null),
                },
                new Timestamp?[] { null }));
        return Assert.Single(Assert.Single(getResult.Batches).Entities).Relationships;
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
