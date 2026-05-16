using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.Tests;

public sealed class MergeProcessingDataAccessLayerTests : DataAccessLayerNonQueryTests
{
    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new MergeProcessingDataAccessLayer(new InMemoryDataAccessLayer());
    }

    [Fact]
    public async Task JsonPatch_ReplaceField_UpdatesEntity()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        null,
                        this.CreateEntityWithTitle("one", "original"),
                        EntityChangeMode.Replace),
                }));
        var tag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var patchResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Patch replace title"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        tag,
                        this.CreatePatch(
                            """
                            { "op": "replace", "path": "/title", "value": "updated" }
                            """),
                        EntityChangeMode.JsonPatch),
                }));

        var result = Assert.Single(patchResult.EntityResults);
        Assert.Equal(UpdateState.Updated, result.UpdateState);
        Assert.Equal(ConcurrencyMatchState.Matched, result.ConcurrencyMatchState);

        var snapshot = await this.GetLatestSnapshotAsync(dataAccessLayer);
        Assert.Equal("updated", this.GetTitle(snapshot.Data));
    }

    [Fact]
    public async Task JsonPatch_AddToArray_AppendsWithDashPath()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        null,
                        this.CreateEntityWithTags("one", new[] { "a", "b" }),
                        EntityChangeMode.Replace),
                }));
        var tag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var patchResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Patch add tag"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        tag,
                        this.CreatePatch(
                            """
                            { "op": "add", "path": "/tags/-", "value": "c" }
                            """),
                        EntityChangeMode.JsonPatch),
                }));
        var updatedTag = Assert.Single(patchResult.EntityResults).ConcurrencyTag!.Value;

        var removeResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Patch remove tag"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        updatedTag,
                        this.CreatePatch(
                            """
                            { "op": "remove", "path": "/tags/0" }
                            """),
                        EntityChangeMode.JsonPatch),
                }));
        Assert.Equal(UpdateState.Updated, Assert.Single(removeResult.EntityResults).UpdateState);

        var snapshot = await this.GetLatestSnapshotAsync(dataAccessLayer);
        var tags = snapshot.Data!.Value.GetProperty("tags").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.Equal(new[] { "b", "c" }, tags);
    }

    [Fact]
    public async Task JsonPatch_WithStaleConcurrencyTag_FailsAndReturnsCurrentEntity()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        null,
                        this.CreateEntityWithTitle("one", "original"),
                        EntityChangeMode.Replace),
                }));
        var initialTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var updateResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Update entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        initialTag,
                        this.CreateEntityWithTitle("one", "current"),
                        EntityChangeMode.Replace),
                }));
        var currentTag = Assert.Single(updateResult.EntityResults).ConcurrencyTag!.Value;

        var stalePatchResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Stale patch"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        initialTag,
                        this.CreatePatch(
                            """
                            { "op": "replace", "path": "/title", "value": "stale" }
                            """),
                        EntityChangeMode.JsonPatch),
                }));

        var failed = Assert.Single(stalePatchResult.EntityResults);
        Assert.Equal(UpdateState.Failed, failed.UpdateState);
        Assert.Equal(ConcurrencyMatchState.NotMatched, failed.ConcurrencyMatchState);
        Assert.Equal(currentTag, failed.ConcurrencyTag);
        Assert.NotNull(failed.CurrentEntity);
        Assert.Equal("current", this.GetTitle(failed.CurrentEntity!.Data));
    }

    [Fact]
    public async Task JsonPatch_WithMissingEntityId_Fails()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var result = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Patch missing entity id"),
                new[]
                {
                    CreateEntityChange(
                        null,
                        null,
                        this.CreatePatch(
                            """
                            { "op": "replace", "path": "/title", "value": "x" }
                            """),
                        EntityChangeMode.JsonPatch),
                }));

        var failed = Assert.Single(result.EntityResults);
        Assert.Equal(UpdateState.Failed, failed.UpdateState);
        Assert.Contains(failed.Errors, error => error.Message.Contains("entity-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultipleReplaceChanges_ForSameEntity_AreCoalescedIntoSingleUpdate()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        null,
                        this.CreateEntityWithTitle("one", "initial"),
                        EntityChangeMode.Replace),
                }));
        var tag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;
        var createTime = (await this.GetSingleHistoryAsync(dataAccessLayer)).UpdateTimes.Single();

        var coalescedResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Two replaces same entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        tag,
                        this.CreateEntityWithTitle("one", "intermediate"),
                        EntityChangeMode.Replace),
                    CreateEntityChange(
                        SampleEntityId,
                        tag,
                        this.CreateEntityWithTitle("one", "final"),
                        EntityChangeMode.Replace),
                }));

        var entityResult = Assert.Single(coalescedResult.EntityResults);
        Assert.Equal(UpdateState.Updated, entityResult.UpdateState);

        var latest = await this.GetLatestSnapshotAsync(dataAccessLayer);
        Assert.Equal("final", this.GetTitle(latest.Data));

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        var updateTimes = history.UpdateTimes.ToArray();
        Assert.Equal(2, updateTimes.Length);
        Assert.Equal(createTime, updateTimes[0]);
    }

    [Fact]
    public async Task MultipleJsonPatchChanges_ForSameEntity_AreCoalescedIntoSingleUpdate()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Create entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        null,
                        this.CreateEntityWithTitle("one", "initial"),
                        EntityChangeMode.Replace),
                }));
        var tag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;
        var createTime = (await this.GetSingleHistoryAsync(dataAccessLayer)).UpdateTimes.Single();

        var coalescedPatchResult = await dataAccessLayer.UpdateAsync(
            CreateUpdateRequest(
                CreateUpdateMetadata("Two patches same entity"),
                new[]
                {
                    CreateEntityChange(
                        SampleEntityId,
                        tag,
                        this.CreatePatch(
                            """
                            { "op": "replace", "path": "/title", "value": "updated" }
                            """),
                        EntityChangeMode.JsonPatch),
                    CreateEntityChange(
                        SampleEntityId,
                        tag,
                        this.CreatePatch(
                            """
                            { "op": "add", "path": "/tags", "value": ["a"] }
                            """),
                        EntityChangeMode.JsonPatch),
                }));

        var entityResult = Assert.Single(coalescedPatchResult.EntityResults);
        Assert.Equal(UpdateState.Updated, entityResult.UpdateState);

        var latest = await this.GetLatestSnapshotAsync(dataAccessLayer);
        Assert.Equal("updated", this.GetTitle(latest.Data));
        var tags = latest.Data!.Value.GetProperty("tags").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.Equal(new[] { "a" }, tags);

        var history = await this.GetSingleHistoryAsync(dataAccessLayer);
        var updateTimes = history.UpdateTimes.ToArray();
        Assert.Equal(2, updateTimes.Length);
        Assert.Equal(createTime, updateTimes[0]);
    }

    private JsonElement CreateEntityWithTitle(
        string name,
        string title)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{SampleEntityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"],
              "title": "{{title}}"
            }
            """);

        return document.RootElement.Clone();
    }

    private JsonElement CreateEntityWithTags(
        string name,
        IReadOnlyCollection<string> tags)
    {
        var tagList = string.Join(", ", tags.Select(static tag => $"\"{tag}\""));
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{SampleEntityId.Value:D}}",
              "entity-types": ["entity"],
              "names": ["{{name}}"],
              "tags": [{{tagList}}]
            }
            """);

        return document.RootElement.Clone();
    }

    private JsonElement CreatePatch(
        string operationObject)
    {
        using var document = JsonDocument.Parse(
            $$"""
            [
              {{operationObject}}
            ]
            """);

        return document.RootElement.Clone();
    }

    private string? GetTitle(
        JsonElement? data)
    {
        if (data is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : null;
    }

    private async Task<EntitySnapshot> GetLatestSnapshotAsync(
        IDataAccessLayer dataAccessLayer)
    {
        return Assert.Single(
            Assert.Single(
                (await dataAccessLayer.GetAsync(
                    CreateGetRequest(
                        new[]
                        {
                            CreateGetEntityRequest(
                                SampleEntityId,
                                null,
                                null,
                                null),
                        },
                        null,
                        new Timestamp?[] { null })))
                .Batches)
            .Entities);
    }

    private async Task<EntityHistoryEntry> GetSingleHistoryAsync(
        IDataAccessLayer dataAccessLayer)
    {
        return Assert.Single(
            (await dataAccessLayer.GetHistoryAsync(
                CreateGetHistoryRequest(new[] { SampleEntityId })))
            .History);
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

    private static GetHistoryRequest CreateGetHistoryRequest(
        IReadOnlyCollection<EntityId> entityIds)
    {
        return new GetHistoryRequest
        {
            EntityIds = entityIds,
        };
    }
}
