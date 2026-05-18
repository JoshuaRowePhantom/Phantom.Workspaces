using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public abstract class DataAccessLayerNonQueryTests : DataAccessLayerNonQueryWithoutHistoryTests
{
    [Fact]
    public async Task Populate_UpdateEntity_GetWithMultipleTimestamps_ReturnsVersionPerTimestamp()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("one")) },
            });
        var createTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var updateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("two"), createTag) },
            });
        Assert.Equal(UpdateState.Updated, Assert.Single(updateResult.EntityResults).UpdateState);

        var historyAfterUpdate = Assert.Single(
            (await dataAccessLayer.GetHistoryAsync(
                new GetHistoryRequest
                {
                    EntityIds = new[] { SampleEntityId },
                }))
            .History);
        var updateTimes = historyAfterUpdate.UpdateTimes.ToArray();
        Assert.Equal(2, updateTimes.Length);
        var createTime = updateTimes[0];
        var updateTime = updateTimes[1];

        var multiTimestampGet = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = SampleEntityId,
                    },
                ],
                Timestamps = new Timestamp?[] { createTime, updateTime },
            });

        var batches = multiTimestampGet.Batches.ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(createTime, batches[0].Timestamp);
        Assert.Equal(updateTime, batches[1].Timestamp);

        var createSnapshot = Assert.Single(batches[0].Entities);
        var updateSnapshot = Assert.Single(batches[1].Entities);
        Assert.Contains("\"one\"", createSnapshot.Data?.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"two\"", updateSnapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Populate_UpdateEntity_TimestampFenceposts_AreInclusiveAndExclusiveAsExpected()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("one")) },
            });
        var createTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var updateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("two"), createTag) },
            });
        Assert.Equal(UpdateState.Updated, Assert.Single(updateResult.EntityResults).UpdateState);

        var historyAfterUpdate = Assert.Single(
            (await dataAccessLayer.GetHistoryAsync(
                new GetHistoryRequest
                {
                    EntityIds = new[] { SampleEntityId },
                }))
            .History);
        var updateTimes = historyAfterUpdate.UpdateTimes.ToArray();
        Assert.Equal(2, updateTimes.Length);
        var createTime = updateTimes[0];
        var updateTime = updateTimes[1];

        var beforeAnyChangeGet = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = SampleEntityId,
                    },
                ],
                Timestamps = new Timestamp?[] { new(DateTimeOffset.UnixEpoch, "0") },
            });
        Assert.Empty(Assert.Single(beforeAnyChangeGet.Batches).Entities);

        var atCreateTimeGet = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = SampleEntityId,
                    },
                ],
                Timestamps = new Timestamp?[] { createTime },
            });
        var atCreateSnapshot = Assert.Single(Assert.Single(atCreateTimeGet.Batches).Entities);
        Assert.Contains("\"one\"", atCreateSnapshot.Data?.GetRawText(), StringComparison.Ordinal);

        var atUpdateTimeGet = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = SampleEntityId,
                    },
                ],
                Timestamps = new Timestamp?[] { updateTime },
            });
        var atUpdateSnapshot = Assert.Single(Assert.Single(atUpdateTimeGet.Batches).Entities);
        Assert.Contains("\"two\"", atUpdateSnapshot.Data?.GetRawText(), StringComparison.Ordinal);

        var changedAtCreateTime = await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest
            {
                EntityIdTimestamps =
                [
                    new EntityIdTimestamp(SampleEntityId, createTime),
                ],
            });
        Assert.Single(changedAtCreateTime.Entities);

        var changedAtUpdateTime = await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest
            {
                EntityIdTimestamps =
                [
                    new EntityIdTimestamp(SampleEntityId, updateTime),
                ],
            });
        Assert.Empty(changedAtUpdateTime.Entities);
    }

    [Fact]
    public async Task Populate_CreateEntity_RecordsHistoryTimestamp()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("one")) },
            });
        Assert.Equal(UpdateState.Added, Assert.Single(createResult.EntityResults).UpdateState);

        var historyResult = await dataAccessLayer.GetHistoryAsync(
            new GetHistoryRequest
            {
                EntityIds = new[] { SampleEntityId },
            });

        var history = Assert.Single(historyResult.History);
        Assert.Equal(SampleEntityId, history.EntityId);
        Assert.Single(history.UpdateTimes);
    }

    [Fact]
    public async Task Populate_UpdateEntity_SupportsTimestampedGetExportAndChangedEntities()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();
        var exportBefore = await dataAccessLayer.ExportAsync(
            new ExportRequest
            {
                SnapshotTime = null,
            });

        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("one")) },
            });
        var createTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var createTime = Assert.Single(
            Assert.Single(
                (await dataAccessLayer.GetHistoryAsync(
                    new GetHistoryRequest
                    {
                        EntityIds = new[] { SampleEntityId },
                    }))
                .History)
            .UpdateTimes);

        var updateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange(new EntityName("two"), createTag) },
            });
        Assert.Equal(UpdateState.Updated, Assert.Single(updateResult.EntityResults).UpdateState);

        var historyAfterUpdate = Assert.Single(
            (await dataAccessLayer.GetHistoryAsync(
                new GetHistoryRequest
                {
                    EntityIds = new[] { SampleEntityId },
                }))
            .History);
        var updateTimes = historyAfterUpdate.UpdateTimes.ToArray();
        Assert.Equal(2, updateTimes.Length);

        var historicalGet = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = new[]
                {
                    new GetEntityRequest
                    {
                        EntityId = SampleEntityId,
                    },
                },
                Timestamps = new Timestamp?[] { createTime },
            });
        var historicalSnapshot = Assert.Single(Assert.Single(historicalGet.Batches).Entities);
        Assert.Contains("\"one\"", historicalSnapshot.Data?.GetRawText(), StringComparison.Ordinal);

        var exportAfterCreate = await dataAccessLayer.ExportAsync(
            new ExportRequest
            {
                SnapshotTime = createTime,
            });
        Assert.Single(exportAfterCreate.ChangeBatches);

        var exportAll = await dataAccessLayer.ExportAsync(
            new ExportRequest
            {
                SnapshotTime = null,
            });
        Assert.Equal(exportBefore.ChangeBatches.Count + 2, exportAll.ChangeBatches.Count);

        var changedAfterCreate = await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest
            {
                EntityIdTimestamps =
                [
                    new EntityIdTimestamp(SampleEntityId, createTime),
                ],
            });
        Assert.Single(changedAfterCreate.Entities);

        var changedAfterUpdate = await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest
            {
                EntityIdTimestamps =
                [
                    new EntityIdTimestamp(SampleEntityId, updateTimes[1]),
                ],
            });
        Assert.Empty(changedAfterUpdate.Entities);
    }
}
