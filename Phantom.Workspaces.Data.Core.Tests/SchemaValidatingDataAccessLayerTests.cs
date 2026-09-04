using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.Tests;

public sealed class SchemaValidatingDataAccessLayerTests : DataAccessLayerNonQueryTests
{
    private static readonly EntityId TestSchemaEntityId = new(Guid.Parse("2f8e74c1-3e2b-48ea-9e3b-64ff1b9f6321"));
    private static readonly EntityId ValidatedEntityId = new(Guid.Parse("f7c54a5f-ae17-4a3b-b3af-1430f4b1d6d3"));
    private static readonly string TestSchemaName = "https://schemas.workspaces.phantom.to/tests/work-item.json";

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new SchemaValidatingDataAccessLayer(new InMemoryDataAccessLayer());
    }

    [Fact]
    public async Task Update_IsRejected_WhenBaseEntitySchemaIsUnavailable()
    {
        var dataAccessLayer = this.CreateDataAccessLayer();
        var entityId = new EntityId("f24f2d0b-8d48-4e57-b9f6-ef2eccece2b1");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity without available schemas"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "names": [["missing-schema-entity"]]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("could not be resolved", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("entity.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_Succeeds_WhenSchemaIsInUnderlyingRepository()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var schemaUpdateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add schema"),
                new[] { CreateSchemaEntityChange(TestSchemaEntityId, TestSchemaName) }));
        Assert.True(schemaUpdateResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(schemaUpdateResult));
        Assert.Equal(UpdateState.Added, schemaUpdateResult.EntityResults.Single().UpdateState);

        var entityUpdateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add validated entity"),
                new[] { CreateValidatedEntityChange(ValidatedEntityId, "one", TestSchemaName) }));

        var result = Assert.Single(entityUpdateResult.EntityResults);
        Assert.True(
            result.UpdateState == UpdateState.Added,
            string.Join(Environment.NewLine, result.Errors.Select(error => error.Message)));
        Assert.Equal(ConcurrencyMatchState.Matched, result.ConcurrencyMatchState);
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityUsesUnknownEntityType()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("3f02d2e0-f8d1-4ec9-8ef8-1f5fd4f8dbd8");

        var result = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity with unknown type"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "profile"],
                              "names": [["unknown-typed-entity"]]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("not a registered entity type", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("profile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityUsesDiscriminatorWithSchemaButNoEntityTypeEntity()
    {
        // A json-schema entity whose name is ["entity-types","orphan-schema"] but that does NOT
        // declare "entity-type" in its entity-types is NOT a registered entity type. An entity
        // that lists "orphan-schema" as a discriminator must therefore be rejected.
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var orphanSchemaEntityId = new EntityId("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var entityId = new EntityId("b2c3d4e5-f6a7-8901-bcde-f01234567891");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add orphan json-schema (no entity-type in entity-types)"),
                new[]
                {
                    CreateEntityChange(
                        orphanSchemaEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{orphanSchemaEntityId}}",
                              "entity-types": ["entity", "json-schema"],
                              "names": [["entity-types", "orphan-schema"]],
                              "schema": {
                                "$id": "https://schemas.workspaces.phantom.to/tests/orphan.json",
                                "type": "object",
                                "properties": {
                                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                                },
                                "required": ["entity-types"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        var result = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity claiming the orphan schema type"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "orphan-schema"],
                              "names": [["orphan-schema-entity"]]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("not a registered entity type", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("orphan-schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_Succeeds_WhenEntityTypeEntityAndEntityUsingItAreInSameRequest()
    {
        // Writing a new entity-type entity and an entity that uses that type in the same
        // request must succeed; the discriminator is "in-flight" within the request.
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityTypeEntityId = new EntityId("c3d4e5f6-a7b8-9012-cdef-012345678902");
        var entityId = new EntityId("d4e5f6a7-b8c9-0123-defa-123456789013");
        const string newTypeName = "inline-new-type";
        const string newTypeSchemaName = "https://schemas.workspaces.phantom.to/tests/inline-new-type.json";

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity-type entity and entity using it together"),
                new[]
                {
                    CreateEntityChange(
                        entityTypeEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityTypeEntityId}}",
                              "entity-types": ["entity", "entity-type", "json-schema"],
                              "names": [
                                ["json-schemas", "{{newTypeSchemaName}}"],
                                ["entity-types", "{{newTypeName}}"]
                              ],
                              "schema": {
                                "$id": "{{newTypeSchemaName}}",
                                "type": "object",
                                "properties": {
                                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                                },
                                "required": ["entity-types"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "{{newTypeName}}"],
                              "names": [["new-type-entity"]]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.Equal(2, result.EntityResults.Count);
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task UpdateAsync_BuildsEntityTypeNamesOnce_AcrossMultipleNonEntityTypeUpdates()
    {
        var inner = new InMemoryDataAccessLayer();
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(inner)).Populate();
        Assert.Empty(populationErrors);

        var counter = new QueryCountingDataAccessLayer(inner);
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(counter);

        var taskEntityId1 = new EntityId("e5f6a7b8-c9d0-1234-efab-234567890124");
        var taskEntityId2 = new EntityId("e5f6a7b8-c9d0-1234-efab-234567890125");
        var taskEntityId3 = new EntityId("e5f6a7b8-c9d0-1234-efab-234567890126");

        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("write 1"), new[] { CreateTaskEntityChange(taskEntityId1) }));
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("write 2"), new[] { CreateTaskEntityChange(taskEntityId2) }));
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("write 3"), new[] { CreateTaskEntityChange(taskEntityId3) }));

        // The json-schema query that also loads entity-type entities is only run once.
        Assert.Equal(1, counter.JsonSchemaQueryCount);
    }

    [Fact]
    public async Task UpdateAsync_InvalidatesEntityTypeCache_WhenEntityTypeEntityIsWritten()
    {
        var inner = new InMemoryDataAccessLayer();
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(inner)).Populate();
        Assert.Empty(populationErrors);

        var counter = new QueryCountingDataAccessLayer(inner);
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(counter);

        var taskEntityId = new EntityId("f6a7b8c9-d0e1-2345-fabc-345678901235");
        var entityTypeEntityId = new EntityId("f6a7b8c9-d0e1-2345-fabc-345678901236");
        var taskEntityId2 = new EntityId("f6a7b8c9-d0e1-2345-fabc-345678901237");
        const string invalidationTestSchemaName = "https://schemas.workspaces.phantom.to/tests/entity-type-cache-invalidation.json";

        // First non-schema write: warms cache (count = 1)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("non-schema write"), new[] { CreateTaskEntityChange(taskEntityId) }));
        Assert.Equal(1, counter.JsonSchemaQueryCount);

        // Entity-type entity write: invalidates cache on success (count stays 1)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("entity-type write"), new[]
        {
            CreateEntityChange(
                entityTypeEntityId,
                null,
                JsonDocument.Parse($$"""
                {
                  "entity-id": "{{entityTypeEntityId}}",
                  "entity-types": ["entity", "entity-type", "json-schema"],
                  "names": [
                    ["json-schemas", "{{invalidationTestSchemaName}}"],
                    ["entity-types", "invalidation-test-type"]
                  ],
                  "schema": {
                    "$id": "{{invalidationTestSchemaName}}",
                    "type": "object",
                    "properties": {
                      "entity-types": { "type": "array", "contains": { "const": "entity" } }
                    },
                    "required": ["entity-types"]
                  }
                }
                """).RootElement.Clone(),
                EntityChangeMode.Replace),
        }));
        Assert.Equal(1, counter.JsonSchemaQueryCount);

        // Second non-schema write: cache was invalidated, rebuilds (count = 2)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("non-schema write 2"), new[] { CreateTaskEntityChange(taskEntityId2) }));
        Assert.Equal(2, counter.JsonSchemaQueryCount);
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityDeclaresOnlyAbstractEntityTypes()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("b2c7e2a1-3f44-4c9e-9a1d-7e2f6b5c4d3a");
        var otherEntityId = new EntityId("a1b2c3d4-e5f6-4071-8293-a4b5c6d7e8f9");

        var result = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity with only abstract types"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "relationship"],
                              "names": [["relationship", "abstract-only"]],
                              "participants": {
                                "entities": ["{{entityId}}", "{{otherEntityId}}"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("only abstract entity types", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_Succeeds_WhenEntityDeclaresConcreteTypeAlongsideAbstractTypes()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("d4e9f1c2-5a6b-4c7d-8e9f-0a1b2c3d4e5f");
        var otherEntityId = new EntityId("f6a7b8c9-0d1e-4f23-8456-7a8b9c0d1e2f");

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add relationship with concrete subtype"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "relationship", "related"],
                              "names": [["relationship", "concrete-related"]],
                              "participants": {
                                "entities": ["{{entityId}}", "{{otherEntityId}}"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        Assert.Equal(UpdateState.Added, result.EntityResults.Single().UpdateState);
    }

    [Fact]
    public async Task Update_ValidatesLocalizedMimeAttachmentContent_ForNote()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("1ac2b418-c4bf-4df9-9f25-6b3d9ab2d7f7");

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add localized note content"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            """
                            {
                              "entity-id": "1ac2b418-c4bf-4df9-9f25-6b3d9ab2d7f7",
                              "entity-types": ["entity", "note"],
                              "names": [["documentation","localized-note"]],
                              "display-name": "Localized Note",
                              "content": {
                                "default": {
                                  "mime-type": "text/markdown",
                                  "url": "documentation/getting-started.md"
                                },
                                "fr-FR": {
                                  "mime-type": "text/markdown",
                                  "content": {
                                    "text": "# Bonjour"
                                  }
                                }
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(
            result.EntityResults.All(static entityResult => entityResult.UpdateState != UpdateState.Failed),
            UpdateResultDiagnostics.Describe(result));
    }

    [Fact]
    public async Task Update_Succeeds_WhenSchemaIsProvidedInSameUpdate()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add schema and entity"),
                new EntityChange[]
                {
                    CreateValidatedEntityChange(ValidatedEntityId, "one", TestSchemaName),
                    CreateSchemaEntityChange(TestSchemaEntityId, TestSchemaName),
                }));

        Assert.True(
            result.EntityResults.Count == 2,
            string.Join(
                Environment.NewLine,
                result.EntityResults.SelectMany(entityResult => entityResult.Errors.Select(error => error.Message))));
        Assert.All(result.EntityResults, entityResult => Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState));
        Assert.DoesNotContain(result.EntityResults, entityResult => entityResult.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task Update_Succeeds_WhenSchemaDeclaresCustomAnnotationKeyword()
    {
        // Custom "x-" annotation keywords (such as x-field-status, x-entity-types and
        // x-default-mime-type) must be legal JSON Schema. The schema dialect permits unknown
        // keywords so these annotations are preserved for the field-type resolver instead of being
        // stripped or rejected when the schema is built.
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add schema with custom annotation keyword and entity"),
                new EntityChange[]
                {
                    CreateValidatedEntityChange(ValidatedEntityId, "one", TestSchemaName),
                    CreateSchemaEntityChangeWithCustomAnnotation(TestSchemaEntityId, TestSchemaName),
                }));

        Assert.Equal(2, result.EntityResults.Count);
        Assert.DoesNotContain(result.EntityResults, entityResult => entityResult.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task Update_Succeeds_WhenRequestSchemaOverridesPrepopulatedSchema()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId("8b9d7bd5-bf9d-4e11-b8d9-4da7cf7df6d6");
        var validatedEntityId = new EntityId("cb40af7f-ff6b-4d7b-98a1-3482ccf2d355");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add prepopulated schema"),
                new[] { CreateSchemaEntityChange(schemaEntityId, TestSchemaName, "string") }));
        var currentSchema = Assert.Single(
            Assert.Single(
                    (await dataAccessLayer.GetAsync(
                        CreateGetRequest(
                            new[] { CreateGetEntityRequest(schemaEntityId, null, null, null) },
                            null,
                            new Timestamp?[] { null })))
                .Batches)
                .Entities);

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Override schema in request and validate entity"),
                new EntityChange[]
                {
                    CreateValidatedEntityChange(validatedEntityId, 123, TestSchemaName),
                    CreateSchemaEntityChange(schemaEntityId, currentSchema.ConcurrencyTag, TestSchemaName, "integer"),
                }));

        Assert.DoesNotContain(result.EntityResults, static entityResult => entityResult.UpdateState == UpdateState.Failed);
        var validatedEntityResult = Assert.Single(result.EntityResults, entityResult => entityResult.ResultingEntityId == validatedEntityId);
        Assert.Equal(UpdateState.Added, validatedEntityResult.UpdateState);
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityViolatesRequestUpdatedSchema()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId("76c5ec45-4950-47c6-b5d6-2e70ff8872f8");
        var validAgainstStoredSchemaEntityId = new EntityId("ec5bb277-d37d-491e-955e-7448be062e5b");
        var validatedEntityId = new EntityId("2d64af62-f994-49f0-935e-6fa0384ee4b5");

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add prepopulated schema"),
                new[] { CreateSchemaEntityChange(schemaEntityId, TestSchemaName, "string") }));
        var currentSchema = Assert.Single(
            Assert.Single(
                    (await dataAccessLayer.GetAsync(
                        CreateGetRequest(
                            new[] { CreateGetEntityRequest(schemaEntityId, null, null, null) },
                            null,
                            new Timestamp?[] { null })))
                .Batches)
                .Entities);

        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Entity is valid against currently stored schema"),
                new[] { CreateValidatedEntityChange(validAgainstStoredSchemaEntityId, "one", TestSchemaName) }));

        var result = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Override schema in request and fail entity validation"),
                new EntityChange[]
                {
                    CreateValidatedEntityChange(validatedEntityId, "one", TestSchemaName),
                    CreateSchemaEntityChange(schemaEntityId, currentSchema.ConcurrencyTag, TestSchemaName, "integer"),
                }));

        var failedResult = Assert.Single(result.EntityResults);
        Assert.Equal(validatedEntityId, failedResult.ResultingEntityId);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Update_Succeeds_WhenSchemaEntityTypeIsUsedAsJsonSchema()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var schemaEntityId = new EntityId("a8428673-25f5-4c30-8f70-c3014f660d95");
        var entityId = new EntityId("ad6f2d02-2f5b-4e64-bec4-78d16dff7f92");
        const string schemaName = "https://schemas.workspaces.phantom.to/tests/implicit-json-schema-entity-type.json";

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity-type schema and validated entity"),
                new EntityChange[]
                {
                    CreateEntityChange(
                        schemaEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{schemaEntityId}}",
                              "entity-types": ["entity", "entity-type", "json-schema"],
                              "names": [["json-schemas", "{{schemaName}}"]],
                              "schema": {
                                "$id": "{{schemaName}}",
                                "type": "object",
                                "properties": {
                                  "title": { "type": "string" },
                                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                                },
                                "required": ["title", "entity-types"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "$schema": "{{schemaName}}",
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "task"],
                              "names": [["validated-entity"]],
                              "title": "valid"
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(
            result.EntityResults.Count == 2,
            string.Join(
                Environment.NewLine,
                result.EntityResults.SelectMany(entityResult => entityResult.Errors.Select(error => error.Message))));
        Assert.DoesNotContain(result.EntityResults, entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.All(result.EntityResults, entityResult => Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState));
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityFailsValidation()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var schemaUpdateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add schema"),
                new[] { CreateSchemaEntityChange(TestSchemaEntityId, TestSchemaName) }));
        Assert.True(schemaUpdateResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(schemaUpdateResult));
        Assert.Equal(UpdateState.Added, schemaUpdateResult.EntityResults.Single().UpdateState);

        var invalidUpdateResult = await RequireUpdateFailsAsync(
            dataAccessLayer,
            CreateUpdateRequest(
                CreateUpdateMetadata("Add invalid entity"),
                new[]
                {
                    CreateValidatedEntityChange(ValidatedEntityId, 123, TestSchemaName),
                }));

        Assert.True(invalidUpdateResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(invalidUpdateResult));
        var failedResult = invalidUpdateResult.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failedResult.ConcurrencyMatchState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("type", StringComparison.OrdinalIgnoreCase));

        var getResult = await dataAccessLayer.GetAsync(
            CreateGetRequest(
                new[]
                {
                    CreateGetEntityRequest(
                        ValidatedEntityId,
                        null,
                        null,
                        null),
                },
                null,
                new Timestamp?[] { null }));
        Assert.Empty(Assert.Single(getResult.Batches).Entities);
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityContainsExtraProperty()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("d9ad4a78-10a9-4e4c-a7e8-0b71495fe6a5");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity with extra property"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "task"],
                              "names": [["entity-with-extra-property"]],
                              "unexpected-property": "should-fail"
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
        // Do not remove: this preserves the closed-world entity contract and prevents silent schema drift.
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("unexpected-property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityTypeEntityContainsPropertyOutsideEntityAndEntityTypeSchemas()
    {
        // Do not remove: this verifies SchemaValidatingDataAccessLayer composes schemas across
        // entity-types ("entity" + "entity-type") and rejects properties that are not defined
        // in the union of those schemas.
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityTypeEntityId = new EntityId("930bc3f8-ddbe-4884-bfba-7a405c209cc9");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add invalid entity-type entity with extra property"),
                new[]
                {
                    CreateEntityChange(
                        entityTypeEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityTypeEntityId}}",
                              "entity-types": ["entity", "entity-type", "json-schema"],
                              "names": [["entity-types","sample-entity-type"]],
                              "schema": {
                                "type": "object",
                                "properties": {
                                  "title": { "type": "string" },
                                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                                },
                                "required": ["entity-types"]
                              },
                              "unexpected-property": "should-fail"
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("unexpected-property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_Succeeds_WhenEntityTypeEntityContainsEntityTypeSpecificFields()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityTypeEntityId = new EntityId("1d033c1d-f265-4b62-866f-ab88ceb66dcf");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add valid entity-type entity"),
                new[]
                {
                    CreateEntityChange(
                        entityTypeEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityTypeEntityId}}",
                              "entity-types": ["entity", "entity-type", "json-schema"],
                              "names": [["entity-types","sample-entity-type"]],
                              "schema": {
                                "type": "object",
                                "properties": {
                                  "title": { "type": "string" },
                                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                                },
                                "required": ["entity-types"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var entityResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Added, entityResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState);
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityOmitsBaseEntityType()
    {
        // Every entity must declare the base "entity" type; entity.json enforces this via
        // entity-types contains "entity".
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("c0a4d0f9-6d2b-4d2e-8e6d-7d8e0a4f1b2c");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity missing the base entity type"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": [],
                              "names": [["missing-base-entity-type"]]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        var failedResult = Assert.Single(result.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("entity-types", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityTypeDefinitionOmitsJsonSchemaType()
    {
        // entity-type.json composes json-schema.json, which requires every entity-type
        // definition to declare the base "json-schema" type.
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("b1f2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity-type definition missing the json-schema type"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "entity-type"],
                              "names": [["entity-types","sample-missing-json-schema"]],
                              "schema": {
                                "type": "object",
                                "properties": {
                                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                                },
                                "required": ["entity-types"]
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        var failedResult = Assert.Single(result.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_IsRejected_WhenEntityTypeSchemaOmitsSelfTypeRule()
    {
        // The entity-type.json meta-rule requires every entity-type schema's inline schema to
        // enforce its own type via required entity-types and a entity-types contains.const.
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var entityId = new EntityId("d2e3f4a5-6b7c-4d8e-9f0a-1b2c3d4e5f60");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add entity-type definition whose schema lacks the self-type rule"),
                new[]
                {
                    CreateEntityChange(
                        entityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{entityId}}",
                              "entity-types": ["entity", "entity-type", "json-schema"],
                              "names": [["entity-types","sample-missing-self-rule"]],
                              "schema": {
                                "type": "object",
                                "properties": {
                                  "title": { "type": "string" }
                                }
                              }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        var failedResult = Assert.Single(result.EntityResults);
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_WhenValidationFails_ErrorMessageIncludesDiagnosticDetails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var invalidEntityId = new EntityId("3f8f4f4d-1664-4f6d-a74a-e5f9f3ccd67f");

        var updateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add schema and invalid entity"),
                new EntityChange[]
                {
                    CreateSchemaEntityChange(TestSchemaEntityId, TestSchemaName),
                    CreateEntityChange(
                        invalidEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "$schema": "{{TestSchemaName}}",
                              "entity-id": "{{invalidEntityId}}",
                              "names": [["invalid-work-item"]],
                              "title": 123
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(updateResult.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(updateResult));
        var failedResult = updateResult.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        var error = Assert.Single(
            failedResult.Errors,
            static updateError => updateError.Message.Contains("does not conform to schema", StringComparison.Ordinal));
        Assert.Contains("does not conform to schema", error.Message, StringComparison.Ordinal);
        Assert.Contains("type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_ValidatesEntitiesByEntityTypeSchema_WhenSchemaPropertyIsMissing()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var viewEntityId = new EntityId("9a73ed76-9ef3-4c6a-8adf-d4884bc8bcbf");

        var invalidResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add invalid view entity by type"),
                new[]
                {
                    CreateEntityChange(
                        viewEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{viewEntityId}}",
                              "entity-types": ["entity", "view"],
                              "names": [["views","invalid-view-by-type"]],
                              "sub-views": []
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));
        Assert.Equal(UpdateState.Failed, Assert.Single(invalidResult.EntityResults).UpdateState);
        Assert.Contains(
            Assert.Single(invalidResult.EntityResults).Errors,
            error => error.Message.Contains("required", StringComparison.OrdinalIgnoreCase));

        var validResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add valid view entity by type"),
                new[]
                {
                    CreateEntityChange(
                        viewEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{viewEntityId}}",
                              "entity-types": ["entity", "view"],
                              "names": [["views","valid-view-by-type"]],
                              "title": { "default": "Valid View" },
                              "sub-views": []
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));
        Assert.DoesNotContain(validResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task Update_ValidatesWorkspaceTabContent_ForEntityTargetAndBrowserUrl()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var workspaceEntityId = new EntityId("24a2ab29-2d0e-4a0c-9f0e-2a1a5c37e5a5");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add workspace with tab content"),
                new[]
                {
                    CreateEntityChange(
                        workspaceEntityId,
                        null,
                        JsonDocument.Parse(
                            """
                            {
                              "entity-id": "24a2ab29-2d0e-4a0c-9f0e-2a1a5c37e5a5",
                              "entity-types": ["entity", "workspace"],
                              "names": [["workspaces/workspace-one"]],
                              "display-name": "Workspace One",
                              "regions": [
                                {
                                  "region-id": "center",
                                  "title": "Center",
                                  "dock": "center",
                                  "size": 1,
                                  "tabs": [
                                    {
                                      "tab-id": "note-tab",
                                      "title": "Note",
                                      "kind": "entity-view",
                                      "content": {
                                        "target-entity-name": ["documentation","getting-started"]
                                      },
                                      "dock": "full"
                                    },
                                    {
                                      "tab-id": "browser-tab",
                                      "title": "Docs",
                                      "kind": "browser-view",
                                      "content": {
                                        "url": "https://example.com"
                                      },
                                      "dock": "right"
                                    }
                                  ]
                                }
                              ]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(
            result.EntityResults.All(static entityResult => entityResult.UpdateState != UpdateState.Failed),
            UpdateResultDiagnostics.Describe(result));
    }

    [Fact]
    public async Task Update_Succeeds_WhenUserComputerProfileReferencesUseEntityNameArrays()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var profileEntityId = new EntityId("57c90b58-a6a2-4e87-84d4-88fd9cf37758");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add valid user computer profile entity"),
                new[]
                {
                    CreateEntityChange(
                        profileEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{profileEntityId}}",
                              "entity-types": ["entity", "user-computer-profile"],
                              "names": [
                                ["computer-user-profiles", "users", "username", "sample-user", "computers", "hostname", "sample-computer"]
                              ],
                              "computer-reference": ["computers", "hostname", "sample-computer"],
                              "user-reference": ["users", "username", "sample-user"]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var entityResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Added, entityResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState);
    }

    [Fact]
    public async Task Update_IsRejected_WhenUserComputerProfileReferencesUseStrings()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var profileEntityId = new EntityId("bb6665e9-d7c8-4f71-9c94-1f1a52eb9f40");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add invalid user computer profile entity"),
                new[]
                {
                    CreateEntityChange(
                        profileEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{profileEntityId}}",
                              "entity-types": ["entity", "user-computer-profile"],
                              "names": [
                                ["computer-user-profiles", "users", "username", "sample-user", "computers", "hostname", "sample-computer"]
                              ],
                              "computer-reference": "computers/hostname/sample-computer",
                              "user-reference": "users/username/sample-user"
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_Succeeds_WhenUserEntityNamesStartWithUsersPrefix()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var userEntityId = new EntityId("b8ad4f1a-ec7a-4d18-a4b9-a14e39c2f5fd");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add valid user entity"),
                new[]
                {
                    CreateEntityChange(
                        userEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{userEntityId}}",
                              "entity-types": ["entity", "user"],
                              "names": [
                                ["users","upn","user@example.com"],
                                ["users","web","github.com","user@github.com"]
                              ],
                              "display-name": { "default": "Sample User" }
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var entityResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Added, entityResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState);
    }

    [Fact]
    public async Task Update_IsRejected_WhenUserEntityNameDoesNotStartWithUsersPrefix()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var userEntityId = new EntityId("03c39067-1c08-4cf8-b76b-dbe955211375");

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Add invalid user entity"),
                new[]
                {
                    CreateEntityChange(
                        userEntityId,
                        null,
                        JsonDocument.Parse(
                            $$"""
                            {
                              "entity-id": "{{userEntityId}}",
                              "entity-types": ["entity", "user"],
                              "names": [
                                ["people","upn","user@example.com"]
                              ]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("does not conform to schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateAsync_BuildsSchemaRegistryOnce_AcrossMultipleNonSchemaUpdates()
    {
        var inner = new InMemoryDataAccessLayer();
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(inner)).Populate();
        Assert.Empty(populationErrors);

        var counter = new QueryCountingDataAccessLayer(inner);
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(counter);

        var taskEntityId1 = new EntityId("a1b2c3d4-e5f6-4a7b-8c9d-000000000001");
        var taskEntityId2 = new EntityId("a1b2c3d4-e5f6-4a7b-8c9d-000000000002");
        var taskEntityId3 = new EntityId("a1b2c3d4-e5f6-4a7b-8c9d-000000000003");

        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("write 1"), new[] { CreateTaskEntityChange(taskEntityId1) }));
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("write 2"), new[] { CreateTaskEntityChange(taskEntityId2) }));
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("write 3"), new[] { CreateTaskEntityChange(taskEntityId3) }));

        Assert.Equal(1, counter.JsonSchemaQueryCount);
    }

    [Fact]
    public async Task UpdateAsync_InvalidatesCache_WhenJsonSchemaEntityIsWritten()
    {
        var inner = new InMemoryDataAccessLayer();
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(inner)).Populate();
        Assert.Empty(populationErrors);

        var counter = new QueryCountingDataAccessLayer(inner);
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(counter);

        var taskEntityId = new EntityId("b2c3d4e5-f6a7-4b8c-9d0e-000000000001");
        var schemaEntityId = new EntityId("b2c3d4e5-f6a7-4b8c-9d0e-000000000002");
        var taskEntityId2 = new EntityId("b2c3d4e5-f6a7-4b8c-9d0e-000000000003");
        const string cacheTestSchemaName = "https://schemas.workspaces.phantom.to/tests/cache-invalidation.json";

        // First non-schema write: warms the cache (count = 1)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("non-schema write"), new[] { CreateTaskEntityChange(taskEntityId) }));
        Assert.Equal(1, counter.JsonSchemaQueryCount);

        // Schema write: uses the cache for validation, then invalidates it on success (count stays 1)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("schema write"), new[] { CreateSchemaEntityChange(schemaEntityId, cacheTestSchemaName) }));
        Assert.Equal(1, counter.JsonSchemaQueryCount);

        // Second non-schema write: cache was invalidated, rebuilds (count = 2)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("non-schema write 2"), new[] { CreateTaskEntityChange(taskEntityId2) }));
        Assert.Equal(2, counter.JsonSchemaQueryCount);
    }

    [Fact]
    public async Task UpdateAsync_CachedRegistry_ValidatesEntityCorrectly()
    {
        var inner = new InMemoryDataAccessLayer();
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(inner)).Populate();
        Assert.Empty(populationErrors);

        var counter = new QueryCountingDataAccessLayer(inner);
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(counter);

        const string cacheTestSchemaName2 = "https://schemas.workspaces.phantom.to/tests/cache-correctness.json";
        var schemaEntityId2 = new EntityId("c3d4e5f6-a7b8-4c9d-0e1f-000000000001");
        var validEntityId1 = new EntityId("c3d4e5f6-a7b8-4c9d-0e1f-000000000002");
        var validEntityId2 = new EntityId("c3d4e5f6-a7b8-4c9d-0e1f-000000000003");

        // Write schema (cache gets built and then invalidated)
        await RequireUpdateSucceedsAsync(dataAccessLayer, CreateUpdateRequest(CreateUpdateMetadata("add schema"), new[] { CreateSchemaEntityChange(schemaEntityId2, cacheTestSchemaName2) }));

        // Write first valid entity: cache rebuilds
        var result1 = await dataAccessLayer.UpdateAsync(CreateUpdateRequest(CreateUpdateMetadata("valid entity 1"), new[] { CreateValidatedEntityChange(validEntityId1, "hello", cacheTestSchemaName2) }));
        Assert.DoesNotContain(result1.EntityResults, static r => r.UpdateState == UpdateState.Failed);

        // Write second valid entity: uses cached registry
        var queryCountBeforeSecondWrite = counter.JsonSchemaQueryCount;
        var result2 = await dataAccessLayer.UpdateAsync(CreateUpdateRequest(CreateUpdateMetadata("valid entity 2"), new[] { CreateValidatedEntityChange(validEntityId2, "world", cacheTestSchemaName2) }));
        Assert.DoesNotContain(result2.EntityResults, static r => r.UpdateState == UpdateState.Failed);
        Assert.Equal(queryCountBeforeSecondWrite, counter.JsonSchemaQueryCount);
    }

    [Fact]
    public async Task UpdateAsync_WithSchemaChangesInRequest_ValidatesAgainstUpdatedSchemas()
    {
        var inner = new InMemoryDataAccessLayer();
        var schemaAccessor = new SchemaAccessor(inner);
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(inner, schemaAccessor)).Populate();
        Assert.Empty(populationErrors);

        var dal = new SchemaValidatingDataAccessLayer(inner, schemaAccessor);
        var schemaEntityId = new EntityId("d0e1f2a3-b4c5-4d6e-8f0a-1b2c3d4e5f6a");
        var entityId = new EntityId("e1f2a3b4-c5d6-4e7f-9a0b-2c3d4e5f6a7b");

        var result = await RequireUpdateSucceedsAsync(
            dal,
            CreateUpdateRequest(
                CreateUpdateMetadata("Schema + entity in same request"),
                new EntityChange[]
                {
                    CreateSchemaEntityChange(schemaEntityId, TestSchemaName),
                    CreateValidatedEntityChange(entityId, "hello", TestSchemaName),
                }));

        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
        Assert.Equal(2, result.EntityResults.Count);
    }

    [Fact]
    public async Task UpdateAsync_WithNoSchemaChanges_UsesSharedSingletonSchemaAccessor()
    {
        var inner = new InMemoryDataAccessLayer();
        var counter = new QueryCountingDataAccessLayer(inner);
        var schemaAccessor = new SchemaAccessor(counter);
        var populationErrors = await new SchemaPopulator(new SchemaValidatingDataAccessLayer(counter, schemaAccessor)).Populate();
        Assert.Empty(populationErrors);

        var dal = new SchemaValidatingDataAccessLayer(counter, schemaAccessor);

        var id1 = new EntityId("f2a3b4c5-d6e7-4f8a-0b1c-3d4e5f6a7b8c");
        var id2 = new EntityId("a3b4c5d6-e7f8-4a0b-1c2d-4e5f6a7b8c9d");

        await RequireUpdateSucceedsAsync(dal, CreateUpdateRequest(CreateUpdateMetadata("write 1"), new[] { CreateTaskEntityChange(id1) }));
        var countAfterFirst = counter.JsonSchemaQueryCount;

        await RequireUpdateSucceedsAsync(dal, CreateUpdateRequest(CreateUpdateMetadata("write 2"), new[] { CreateTaskEntityChange(id2) }));

        // The shared singleton SchemaAccessor caches schemas — the second update must not re-query.
        Assert.Equal(countAfterFirst, counter.JsonSchemaQueryCount);
    }

    private static EntityChange CreateTaskEntityChange(EntityId entityId)
    {
        using var entityDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "task"],
              "names": [["tasks", "task-{{entityId}}"]]
            }
            """);
        return CreateEntityChange(entityId, null, entityDocument.RootElement.Clone(), EntityChangeMode.Replace);
    }

    private sealed class QueryCountingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
    {
        private int _jsonSchemaQueryCount;

        public int JsonSchemaQueryCount => _jsonSchemaQueryCount;

        public QueryCountingDataAccessLayer(IDataAccessLayer inner)
            : base(inner)
        {
        }

        public override async Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Clauses.Any(static c => c.ClauseIdentifier.Value == "json-schema"))
            {
                Interlocked.Increment(ref _jsonSchemaQueryCount);
            }

            return await base.QueryAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static EntityChange CreateSchemaEntityChange(
        EntityId entityId,
        string schemaName)
    {
        return CreateSchemaEntityChange(entityId, schemaName, "string");
    }

    private static EntityChange CreateSchemaEntityChange(
        EntityId entityId,
        string schemaName,
        string titleType)
    {
        return CreateSchemaEntityChange(entityId, null, schemaName, titleType);
    }

    private static EntityChange CreateSchemaEntityChange(
        EntityId entityId,
        ConcurrencyTag? concurrencyTag,
        string schemaName,
        string titleType)
    {
        using var schemaDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "entity-type", "json-schema"],
              "names": [["json-schemas", "{{schemaName}}"]],
              "schema": {
                "$id": "{{schemaName}}",
                "type": "object",
                "properties": {
                  "title": { "type": "{{titleType}}" },
                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                },
                "required": ["title", "entity-types"]
              }
            }
            """);

        return CreateEntityChange(
            entityId,
            concurrencyTag,
            schemaDocument.RootElement.Clone(),
            EntityChangeMode.Replace);
    }

    private static EntityChange CreateValidatedEntityChange(
        EntityId entityId,
        object title,
        string schemaName)
    {
        using var entityDocument = JsonDocument.Parse(
            $$"""
            {
              "$schema": "{{schemaName}}",
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "task"],
              "names": [["validated-entity"]],
              "title": {{JsonSerializer.Serialize(title)}}
            }
            """);

        return CreateEntityChange(
            entityId,
            null,
            entityDocument.RootElement.Clone(),
            EntityChangeMode.Replace);
    }

    private static EntityChange CreateSchemaEntityChangeWithCustomAnnotation(
        EntityId entityId,
        string schemaName)
    {
        using var schemaDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "entity-type", "json-schema"],
              "names": [["json-schemas", "{{schemaName}}"]],
              "schema": {
                "$id": "{{schemaName}}",
                "type": "object",
                "properties": {
                  "title": {
                    "type": "string",
                    "x-custom-annotation": { "good-status-values": ["one"] }
                  },
                  "entity-types": { "type": "array", "contains": { "const": "entity" } }
                },
                "required": ["title", "entity-types"]
              }
            }
            """);

        return CreateEntityChange(
            entityId,
            null,
            schemaDocument.RootElement.Clone(),
            EntityChangeMode.Replace);
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
}
