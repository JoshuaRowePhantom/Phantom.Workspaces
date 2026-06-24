using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

public sealed class EntityRepositoryTests
{
    [AvaloniaFact]
    public async Task TryGetEntityByName_FindsSeededMainView()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            ct);
        var repository = broker.EntityRepository;
        var snapshots = await repository.ExportEntitySnapshotsAsync(ct);

        var mainView = repository.TryGetEntityByName(
            snapshots,
            new EntityName("views", "main"));

        Assert.NotNull(mainView);
    }

    [AvaloniaFact]
    public async Task ExportEntitySnapshotsAsync_ReturnsLatestEntityVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            ct);
        var repository = broker.EntityRepository;
        var entityId = new EntityId("55555555-5555-5555-5555-555555555555");

        var firstConcurrencyTag = await UpsertEntityAsync(
            repository,
            entityId,
            """
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity", "task"],
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
              "entity-types": ["entity", "task"],
              "names": [["tests", "export-version"]],
              "display-name": { "default": "Version 2" }
            }
            """,
            firstConcurrencyTag);

        var snapshots = await repository.ExportEntitySnapshotsAsync(ct);

        var snapshot = Assert.Contains(entityId, snapshots);
        Assert.Contains("Version 2", snapshot.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CreateAsync_InitializesWorkspaceEntitySessionDiscoveryEntities()
    {
        var repository = await EntityRepository.CreateAsync(new UnknownRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();

        Assert.Contains(repository.WorkspaceEntitySession.UserEntityId, snapshots.Keys);
        Assert.Contains(repository.WorkspaceEntitySession.ComputerEntityId, snapshots.Keys);
        Assert.Contains(repository.WorkspaceEntitySession.UserComputerProfileEntityId, snapshots.Keys);
    }

    private static async Task<ConcurrencyTag?> UpsertEntityAsync(
        EntityRepository repository,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        var ct = TestContext.Current.CancellationToken;
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
            },
            ct);

        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return entityResult.ConcurrencyTag;
    }
}
