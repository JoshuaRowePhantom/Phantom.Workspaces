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
        var sourceEntityId = new EntityId("2ff0a31d-a5a6-4202-bcce-a9ca313f2fa9");
        var destinationEntityId = new EntityId("da1fc548-8dac-484f-b9bc-f53d0483f17c");
        var relationshipEntityId = new EntityId("f460aa7e-a13e-4967-a7e2-87fa072f8d95");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create participant entities"),
                new[]
                {
                    this.CreateEntityChange(sourceEntityId, "source"),
                    this.CreateEntityChange(destinationEntityId, "destination"),
                }));

        var relationshipCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create relationship"),
                new[]
                {
                    this.CreateRelationshipChange(
                        relationshipEntityId,
                        sourceEntityId,
                        destinationEntityId),
                }));
        Assert.True(relationshipCreateResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(relationshipCreateResult));
        Assert.True(
            relationshipCreateResult.EntityResults.Single().UpdateState == UpdateState.Added,
            UpdateResultDiagnostics.Describe(relationshipCreateResult));

        var sourceDeleteTag = (await this.GetEntitySnapshotByIdAsync(dataAccessLayer, sourceEntityId)).ConcurrencyTag;
        var deleteResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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
    public async Task CreateEntity_WithMultiComponentName_CreatesFolderPrefixes()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("14fcceea-04dc-4ed0-a7e2-54649f23ad99");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create nested entity"),
                new[]
                {
                    CreateNamedEntityChange(entityId, null, new[] { "projects", "alpha", "task-1" }),
                }));

        var firstPrefix = await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects"));
        var secondPrefix = await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha"));
        Assert.Single(firstPrefix);
        Assert.Single(secondPrefix);
        Assert.True(HasEntityType(firstPrefix.Single(), "folder"));
        Assert.True(HasEntityType(secondPrefix.Single(), "folder"));
    }

    [Fact]
    public async Task CreateEntity_WithMultipleNames_CreatesFolderPrefixesForEachName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("d301f7ce-b323-46bc-ae98-a03f47866d6c");
        var serializedNames = JsonSerializer.Serialize(
            new[]
            {
                new[] { "projects", "alpha", "task-1" },
                new[] { "users", "upn", "user@example.com" },
            });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": {{serializedNames}}
            }
            """);

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with multiple names"),
                new[]
                {
                    CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace),
                }));

        Assert.True(HasEntityType((await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects"))).Single(), "folder"));
        Assert.True(HasEntityType((await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha"))).Single(), "folder"));
        Assert.True(HasEntityType((await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("users"))).Single(), "folder"));
        Assert.True(HasEntityType((await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("users", "upn"))).Single(), "folder"));
    }

    [Fact]
    public async Task CreateEntity_WithMultipleNames_CreatesFolderPrefixesForEntityTypesAndJsonSchemas()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("85d2c332-f75d-4c23-8c79-3c9ec4c0a928");
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [
                ["entity-types", "custom-type"],
                ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/custom/custom-type.json"]
              ]
            }
            """);

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity-type with multiple names"),
                new[]
                {
                    CreateEntityChange(entityId, null, document.RootElement.Clone(), EntityChangeMode.Replace),
                }));

        Assert.True(HasEntityType((await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("entity-types"))).Single(), "folder"));
        Assert.True(HasEntityType((await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("json-schemas"))).Single(), "folder"));
    }

    [Fact]
    public async Task CreateEntity_WithSingleComponentName_DoesNotCreateFolderWithSameName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("bc9e4f0d-4fd4-4f90-8ecf-96ee8e87095e");
        var entityName = new EntityName("single-component-name");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create single-component named entity"),
                new[]
                {
                    CreateNamedEntityChange(entityId, null, entityName.Components),
                }));

        var snapshots = await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, entityName);
        var snapshot = Assert.Single(snapshots);
        Assert.False(HasEntityType(snapshot, "folder"));
    }

    [Fact]
    public async Task DeleteEntities_DoesNotRemoveExistingFolderEntities()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var firstEntityId = new EntityId("fd464969-b642-42e1-97df-c2ded3aefea2");
        var secondEntityId = new EntityId("5ff79d5e-0800-4836-8ec0-8f52115f3b80");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create nested entities"),
                new[]
                {
                    CreateNamedEntityChange(firstEntityId, null, new[] { "projects", "alpha", "task-1" }),
                    CreateNamedEntityChange(secondEntityId, null, new[] { "projects", "beta", "task-2" }),
                }));

        var firstSnapshot = await this.GetEntitySnapshotByIdAsync(dataAccessLayer, firstEntityId);
        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Delete first nested entity"),
                new[]
                {
                    CreateEntityChange(firstEntityId, firstSnapshot.ConcurrencyTag, null, EntityChangeMode.Replace),
                }));

        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha")));
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects")));
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "beta")));

        var secondSnapshot = await this.GetEntitySnapshotByIdAsync(dataAccessLayer, secondEntityId);
        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Delete second nested entity"),
                new[]
                {
                    CreateEntityChange(secondEntityId, secondSnapshot.ConcurrencyTag, null, EntityChangeMode.Replace),
                }));

        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects")));
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha")));
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "beta")));
    }

    [Fact]
    public async Task DeleteFolder_WithExistingDescendants_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var childEntityId = new EntityId("768d4dbb-5f4e-4773-a8b7-b6f168272f70");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create nested entity"),
                new[]
                {
                    CreateNamedEntityChange(childEntityId, null, new[] { "projects", "alpha", "task-1" }),
                }));

        var projectsFolder = Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects")));
        var deleteFolderResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Delete non-empty folder"),
                new[]
                {
                    CreateEntityChange(
                        projectsFolder.EntityId,
                        projectsFolder.ConcurrencyTag,
                        null,
                        EntityChangeMode.Replace),
                }));

        Assert.Contains(
            deleteFolderResult.EntityResults,
            result => result.RequestedEntityId == projectsFolder.EntityId && result.UpdateState == UpdateState.Failed);
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects")));
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha")));
        Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha", "task-1")));
    }

    [Fact]
    public async Task DeleteFolder_WithDescendantsDeletedInSameTransaction_Succeeds()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var childEntityId = new EntityId("a2f5f82e-9a8a-425f-938d-2122b9b4a467");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create nested entity"),
                new[]
                {
                    CreateNamedEntityChange(childEntityId, null, new[] { "projects", "alpha", "task-1" }),
                }));

        var childEntity = await this.GetEntitySnapshotByIdAsync(dataAccessLayer, childEntityId);
        var alphaFolder = Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha")));
        var projectsFolder = Assert.Single(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects")));

        var deleteResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Delete nested tree in one transaction"),
                new[]
                {
                    CreateEntityChange(childEntity.EntityId, childEntity.ConcurrencyTag, null, EntityChangeMode.Replace),
                    CreateEntityChange(alphaFolder.EntityId, alphaFolder.ConcurrencyTag, null, EntityChangeMode.Replace),
                    CreateEntityChange(projectsFolder.EntityId, projectsFolder.ConcurrencyTag, null, EntityChangeMode.Replace),
                }));

        Assert.DoesNotContain(deleteResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
        Assert.Empty(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects")));
        Assert.Empty(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha")));
        Assert.Empty(await this.GetEntitySnapshotsByNameAsync(dataAccessLayer, new EntityName("projects", "alpha", "task-1")));
    }

    [Fact]
    public async Task TypedEntityReferences_RequireExistingEntityAndMatchingType()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId("1f4ddf45-24f9-460d-9f58-02e3f3f1b278");
        var schemaName = "https://schemas.workspaces.phantom.to/workspaces/tests/typed-reference.json";
        var sourceEntityId = new EntityId("1a51d9a5-72b2-4427-8cd0-1c238b9bb42a");
        var wrongTypeTargetEntityId = new EntityId("6fb49878-7905-4fb8-9032-ec9fd53d6b84");
        var missingTargetEntityId = new EntityId("c33014c9-66b6-4955-b0aa-f4d5ddfd0e76");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create typed-ref schema"),
                new[]
                {
                    this.CreateTypedReferenceSchemaChange(schemaEntityId, schemaName),
                }));

        var missingTargetResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with missing target"),
                new[]
                {
                    this.CreateTypedReferenceEntityChange(sourceEntityId, "source-missing", schemaName, missingTargetEntityId),
                }));
        Assert.True(missingTargetResult.EntityResults.Count > 0, UpdateResultDiagnostics.Describe(missingTargetResult));
        var missingFailure = missingTargetResult.EntityResults.Single(result => result.RequestedEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Failed, missingFailure.UpdateState);
        Assert.Contains(missingFailure.Errors, static error => error.Message.Contains("does not exist", StringComparison.Ordinal));

        var wrongTypeResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with wrong-type target"),
                new[]
                {
                    this.CreateTypedReferenceEntityChange(sourceEntityId, "source-wrong-type", schemaName, wrongTypeTargetEntityId),
                }));
        Assert.True(wrongTypeResult.EntityResults.Count > 0, UpdateResultDiagnostics.Describe(wrongTypeResult));
        var wrongTypeFailure = wrongTypeResult.EntityResults.Single(result => result.RequestedEntityId == sourceEntityId);
        Assert.Equal(UpdateState.Failed, wrongTypeFailure.UpdateState);
        Assert.Contains(wrongTypeFailure.Errors, static error => error.Message.Contains("does not match required types", StringComparison.Ordinal));

        var validTargetEntityId = new EntityId("91e69f56-e57c-4da8-8450-24f40531f730");
        var validTargetCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create valid target"),
                new[]
                {
                    this.CreateEntityChange(validTargetEntityId, "valid-target"),
                }));

        var validCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity with valid target"),
                new[]
                {
                    this.CreateTypedReferenceEntityChange(sourceEntityId, "source-valid", schemaName, validTargetEntityId),
                }));
        Assert.True(
            validCreateResult.EntityResults.Any(static result => result.UpdateState is UpdateState.Added or UpdateState.Updated),
            UpdateResultDiagnostics.Describe(validCreateResult));
        Assert.DoesNotContain(validCreateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task TypedEntityNameReferences_RequireExistingEntityByName()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId("fd643f35-56f5-45f7-882f-0f8c81f17f13");
        var schemaName = "https://schemas.workspaces.phantom.to/workspaces/tests/typed-reference-by-name.json";
        var sourceEntityId = new EntityId("8dc1ff36-74a1-49d1-b11a-0fc0ef1adf2f");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create typed name-ref schema"),
                new[]
                {
                    this.CreateTypedNameReferenceSchemaChange(schemaEntityId, schemaName),
                }));

        var missingTargetResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
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
        Assert.True(missingTargetResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(missingTargetResult));
        var missingFailure = missingTargetResult.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, missingFailure.UpdateState);
        Assert.Contains(missingFailure.Errors, static error => error.Message.Contains("does not exist", StringComparison.Ordinal));

        var validNamedTargetCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create valid named target"),
                new[]
                {
                    this.CreateEntityChange(new EntityId("9af2fdce-e592-40a6-9f38-f3c6c9f68aea"), "valid-target"),
                }));

        var validTargetResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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
        var entityId = new EntityId("c30108d2-4e17-4e8c-98d6-31ebf54db8de");
        var relationshipEntityId = new EntityId("24c03916-f515-497e-af7b-96e3874eb6ec");
        var missingTargetEntityId = new EntityId("f73dc6ec-432c-46e7-af03-cd53c423f5b3");
        var existingParticipantEntityId = new EntityId("11f245dd-8a17-4f8d-a9f6-4c6ef4f3dadf");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create existing participant"),
                new[]
                {
                    this.CreateEntityChange(existingParticipantEntityId, "participant"),
                }));

        var addEntityResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
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

        var addRelationshipResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
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
        var sourceEntityId = new EntityId("d1594aac-208f-4eb6-a88e-4897be2d8cb6");
        var targetEntityId = new EntityId("4fb7bd08-cc2a-4178-b7bf-cdca9f70c1df");
        var otherParticipantEntityId = new EntityId("331f56a2-f15e-4cd3-9740-29df9f274516");
        var relationshipEntityId = new EntityId("74f9f094-5bea-4108-ae85-e6321a349adb");
        var missingTargetEntityId = new EntityId("48c2e6d8-c1cc-4ae8-a846-2127d0a8ecdf");

        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create baseline entities"),
                new[]
                {
                    this.CreateEntityWithSingleReferenceChange(sourceEntityId, null, "source", targetEntityId),
                    this.CreateEntityChange(targetEntityId, "target"),
                    this.CreateEntityChange(otherParticipantEntityId, "other"),
                    this.CreateRelationshipChange(relationshipEntityId, sourceEntityId, otherParticipantEntityId),
                }));

        var sourceConcurrencyTag = createResult.EntityResults.Single(result => result.ResultingEntityId == sourceEntityId).ConcurrencyTag!.Value;
        var relationshipConcurrencyTag = createResult.EntityResults.Single(result => result.ResultingEntityId == relationshipEntityId).ConcurrencyTag!.Value;

        var updateSourceResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
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

        var updateRelationshipResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
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
    public async Task RelationshipEntities_DoNotGenerateManagedReferenceRelationships()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var sourceEntityId = new EntityId("3e16e8c8-51e0-494f-9fc3-b9f3f2f76417");
        var destinationEntityId = new EntityId("f2f4d45b-f328-4c59-9b5c-c95b0ad96be0");
        var referencedEntityId = new EntityId("5ecf03d3-8d52-4ad2-9b9a-ae71f18a91a2");
        var relationshipEntityId = new EntityId("a5eef23b-a4a8-4291-9d78-4ab1099a1ba5");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create participants and reference target"),
                new[]
                {
                    this.CreateEntityChange(sourceEntityId, "source"),
                    this.CreateEntityChange(destinationEntityId, "destination"),
                    this.CreateEntityChange(referencedEntityId, "reference-target"),
                }));

        using var relationshipDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipEntityId}}",
              "entity-types": ["relationship", "related"],
              "names": [["relationship-with-reference-field"]],
              "participants": {
                "entities": ["{{sourceEntityId}}", "{{destinationEntityId}}"]
              },
              "first-entity-id": "{{referencedEntityId}}"
            }
            """);
        var relationshipCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create relationship with additional reference-like field"),
                new[]
                {
                    CreateEntityChange(relationshipEntityId, null, relationshipDocument.RootElement.Clone(), EntityChangeMode.Replace),
                }));
        Assert.DoesNotContain(relationshipCreateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        Assert.Empty(await this.GetReferenceRelationshipsAsync(dataAccessLayer, relationshipEntityId));
        Assert.Empty(await this.GetReferenceRelationshipsAsync(dataAccessLayer, sourceEntityId));
        Assert.Empty(await this.GetReferenceRelationshipsAsync(dataAccessLayer, destinationEntityId));
        Assert.Empty(await this.GetReferenceRelationshipsAsync(dataAccessLayer, referencedEntityId));
    }

    [Fact]
    public async Task ReferenceRelationship_IsRemovedOnlyWhenAllReferencesAreGone()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var sourceEntityId = new EntityId("b82d6a9c-157f-4f2f-a2cd-3f96035e3585");
        var targetEntityId = new EntityId("60a52352-f9fc-4d15-9ca4-a59e4dbafe1f");

        var targetCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Create target"),
                new[]
                {
                    this.CreateEntityChange(targetEntityId, "target"),
                }));
        Assert.True(targetCreateResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(targetCreateResult));
        Assert.Equal(UpdateState.Added, targetCreateResult.EntityResults.Single().UpdateState);

        var sourceCreateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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
        var generatedReferenceRelationship = Assert.Single(relationshipsAfterCreate);
        Assert.True(
            generatedReferenceRelationship.Data is JsonElement generatedReferenceData
            && generatedReferenceData.TryGetProperty("entity-types", out var entityTypes)
            && entityTypes.ValueKind == JsonValueKind.Array
            && entityTypes.EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["relationship", "reference"])
            && generatedReferenceData.TryGetProperty("participants", out var participants)
            && participants.ValueKind == JsonValueKind.Object
            && participants.TryGetProperty("source", out var sourceId)
            && sourceId.ValueKind == JsonValueKind.String
            && string.Equals(sourceId.GetString(), sourceEntityId.ToString(), StringComparison.Ordinal)
            && participants.TryGetProperty("target", out var targetId)
            && targetId.ValueKind == JsonValueKind.String
            && string.Equals(targetId.GetString(), targetEntityId.ToString(), StringComparison.Ordinal),
            "Generated reference relationship shape did not match the tightened schema.");

        var removeOneReferenceResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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

        var removeAllReferencesResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
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
              "entity-id": "{{entityId}}",
              "entity-types": ["{{entityType}}"],
              "names": [["{{name}}"]]
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
              "entity-id": "{{relationshipEntityId}}",
              "entity-types": ["relationship", "related"],
              "names": [["source-to-destination"]],
              "participants": {
                "entities": ["{{sourceEntityId}}", "{{destinationEntityId}}"]
              }
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
              "entity-id": "{{schemaEntityId}}",
              "entity-types": ["entity-type"],
              "names": [["json-schemas", "{{schemaName}}"]],
              "schema": {
                "$id": "{{schemaName}}",
                "type": "object",
                "properties": {
                  "entity-id": {
                    "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-id"
                  },
                  "entity-types": {
                    "type": "array",
                    "items": { "type": "string" }
                  },
                  "names": {
                    "type": "array",
                    "items": {
                      "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-name"
                    }
                  },
                  "target-entity-id": {
                    "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-id",
                    "x-entity-type": "entity"
                  }
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
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["{{name}}"]],
              "target-entity-id": "{{targetEntityId}}"
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
              "entity-id": "{{entityId}}",
              "entity-types": ["entity-type"],
              "names": [["json-schemas", "{{schemaName}}"]],
              "schema": {
                "$id": "{{schemaName}}",
                "type": "object",
                "properties": {
                  "entity-id": {
                    "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-id"
                  },
                  "entity-types": {
                    "type": "array",
                    "items": {
                      "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-type-id"
                    }
                  },
                  "names": {
                    "type": "array",
                    "items": {
                      "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-name"
                    }
                  },
                  "target-entity-name": {
                    "$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/core.json#/$defs/entity-reference",
                    "x-entity-type": "entity"
                  }
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
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["{{name}}"]],
              "target-entity-name": ["{{targetName}}"]
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
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["{{name}}"]],
              "first-entity-id": "{{firstReferenceEntityId}}",
              "second-entity-id": "{{secondReferenceEntityId}}"
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
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["{{name}}"]],
              "first-entity-id": "{{referenceEntityId}}"
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
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["{{name}}"]]
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

    private async Task<IReadOnlyCollection<EntitySnapshot>> GetEntitySnapshotsByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        var getResult = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[]
                {
                    CreateGetEntityRequest(null, entityName, null, null),
                },
                null,
                new Timestamp?[] { null }));
        return Assert.Single(getResult.Batches).Entities;
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
                        new RelationshipTypeNameSet(new[] { "reference" }),
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

    private static EntityChange CreateNamedEntityChange(
        EntityId entityId,
        ConcurrencyTag? concurrencyTag,
        IReadOnlyCollection<string> nameComponents)
    {
        var serializedName = JsonSerializer.Serialize(new[] { nameComponents });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": {{serializedName}}
            }
            """);
        return CreateEntityChange(entityId, concurrencyTag, document.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private static bool HasEntityType(
        EntitySnapshot snapshot,
        string entityType)
    {
        return snapshot.Data is JsonElement data
            && data.TryGetProperty("entity-types", out var entityTypes)
            && entityTypes.ValueKind == JsonValueKind.Array
            && entityTypes.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), entityType, StringComparison.Ordinal));
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
