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
                              "entity-types": ["profile"],
                              "names": ["unknown-typed-entity"]
                            }
                            """).RootElement.Clone(),
                        EntityChangeMode.Replace),
                }));

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var failedResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Failed, failedResult.UpdateState);
        Assert.Contains(failedResult.Errors, error => error.Message.Contains("could not be resolved", StringComparison.Ordinal));
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
                              "entity-types": ["note"],
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
                              "entity-types": ["entity-type"],
                              "names": [["json-schemas", "{{schemaName}}"]],
                              "schema": {
                                "$id": "{{schemaName}}",
                                "type": "object",
                                "properties": {
                                  "title": { "type": "string" }
                                },
                                "required": ["title"]
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
                              "entity-types": ["entity"],
                              "names": ["validated-entity"],
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
                              "entity-types": ["entity"],
                              "names": ["entity-with-extra-property"],
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
                              "entity-types": ["entity-type"],
                              "names": [["entity-types","sample-entity-type"]],
                              "schema": {
                                "type": "object",
                                "properties": {
                                  "title": { "type": "string" }
                                }
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
                              "entity-types": ["entity-type"],
                              "names": [["entity-types","sample-entity-type"]],
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

        Assert.True(result.EntityResults.Count == 1, UpdateResultDiagnostics.Describe(result));
        var entityResult = result.EntityResults.Single();
        Assert.Equal(UpdateState.Added, entityResult.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, entityResult.ConcurrencyMatchState);
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
                              "names": ["invalid-work-item"],
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
                              "entity-types": ["view"],
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
                              "entity-types": ["view"],
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
                              "entity-types": ["workspace"],
                              "names": ["workspaces/workspace-one"],
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
                              "entity-types": ["user"],
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
                              "entity-types": ["user"],
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

    private static EntityChange CreateSchemaEntityChange(
        EntityId entityId,
        string schemaName)
    {
        using var schemaDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity-type"],
              "names": [["json-schemas", "{{schemaName}}"]],
              "schema": {
                "$id": "{{schemaName}}",
                "type": "object",
                "properties": {
                  "title": { "type": "string" }
                },
                "required": ["title"]
              }
            }
            """);

        return CreateEntityChange(
            entityId,
            null,
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
              "entity-types": ["entity"],
              "names": ["validated-entity"],
              "title": {{JsonSerializer.Serialize(title)}}
            }
            """);

        return CreateEntityChange(
            entityId,
            null,
            entityDocument.RootElement.Clone(),
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
