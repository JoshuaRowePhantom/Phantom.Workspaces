using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class EntityRepositoryTests
{
    [Fact]
    public async Task TryGetEntityByName_FindsSeededMainView()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"));
        var repository = broker.EntityRepository;
        var snapshots = await repository.ExportEntitySnapshotsAsync();

        var mainView = repository.TryGetEntityByName(
            snapshots,
            new EntityName("views", "main"));

        Assert.NotNull(mainView);
    }

    [Fact]
    public async Task ExportEntitySnapshotsAsync_ReturnsLatestEntityVersion()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"));
        var repository = broker.EntityRepository;
        var entityId = new EntityId("55555555-5555-5555-5555-555555555555");

        var firstConcurrencyTag = await UpsertEntityAsync(
            repository,
            entityId,
            """
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity"],
              "names": [["tests", "export-version"]],
              "display-name": { "default": "Version 1" }
            }
            """,
            concurrencyTag: null);
        await UpsertEntityAsync(
            repository,
            entityId,
            """
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity"],
              "names": [["tests", "export-version"]],
              "display-name": { "default": "Version 2" }
            }
            """,
            firstConcurrencyTag);

        var snapshots = await repository.ExportEntitySnapshotsAsync();

        var snapshot = Assert.Contains(entityId, snapshots);
        Assert.Contains("Version 2", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task<ConcurrencyTag?> UpsertEntityAsync(
        EntityRepository repository,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await repository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Entity repository test upsert.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });

        var entityResult = Assert.Single(updateResult.EntityResults);
        Assert.Empty(entityResult.Errors);
        return entityResult.ConcurrencyTag;
    }
}
