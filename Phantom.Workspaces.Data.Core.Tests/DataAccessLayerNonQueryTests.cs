using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public abstract class DataAccessLayerNonQueryTests : DataAccessLayerNonQueryWithoutHistoryTests
{
    [Fact]
    public async Task Populate_CreateEntity_RecordsHistoryTimestamp()
    {
        var dataAccessLayer = await this.CreatePopulatedDataAccessLayerAsync();

        var createResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange("one") },
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

        var createResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange("one") },
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

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update entity",
                    },
                },
                Changes = new[] { this.CreateUpsertChange("two", createTag) },
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
