using System.Runtime.CompilerServices;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrokerTests
{
    [Fact]
    public async Task CreateInitializedAsync_PopulatesRepositoryForInMemorySource()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"));

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync();

        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_LoadsBindableEntities()
    {
        var entityId = new EntityId("11111111-1111-1111-1111-111111111111");
        var timestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var snapshot = CreateSnapshot(
            entityId,
            timestamp,
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["entity"],
              "names": ["loaded-entity"],
              "display-name": { "default": "Loaded" }
            }
            """);

        var broker = await CreateBrokerAsync();
        await SeedSnapshotAsync(broker, snapshot);

        var entities = await broker.GetEntitiesAsync([entityId]);

        var entity = Assert.Single(entities);
        Assert.Equal(entityId, entity.EntityId);
        Assert.Equal("Loaded", entity.DisplayName);
        Assert.Equal("entity", entity.EntityType);
    }

    [Fact]
    public async Task RefreshAsync_UpdatesBindableEntityObject()
    {
        var entityId = new EntityId("22222222-2222-2222-2222-222222222222");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity"],
              "names": ["loaded-entity"],
              "display-name": { "default": "Loaded" }
            }
            """);

        var refreshedSnapshot = CreateSnapshot(
            entityId,
            new Timestamp(DateTimeOffset.UtcNow, "2"),
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity"],
              "names": ["loaded-entity"],
              "display-name": { "default": "Updated" }
            }
            """);

        var broker = await CreateBrokerAsync();
        await SeedSnapshotAsync(broker, initialSnapshot);
        var entities = await broker.GetEntitiesAsync([entityId]);
        var entity = Assert.Single(entities);
        var previousModifiedTime = entity.ModifiedTime;

        await SeedSnapshotAsync(broker, refreshedSnapshot, entity.ConcurrencyTag);
        await broker.RefreshAsync();

        Assert.Equal("Updated", entity.DisplayName);
        Assert.NotEqual(previousModifiedTime, entity.ModifiedTime);
        Assert.Contains("\"Updated\"", entity.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_SkipsCollectedSubscriptions()
    {
        var entityId = new EntityId("33333333-3333-3333-3333-333333333333");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity"],
              "names": ["collected-entity"],
              "display-name": { "default": "Collected" }
            }
            """);

        var refreshedSnapshot = CreateSnapshot(
            entityId,
            new Timestamp(DateTimeOffset.UtcNow, "2"),
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity"],
              "names": ["collected-entity"],
              "display-name": { "default": "Updated" }
            }
            """);

        var broker = await CreateBrokerAsync();
        await SeedSnapshotAsync(broker, initialSnapshot);
        var weakEntity = await CreateCollectedEntityAsync(broker, entityId);

        ForceGarbageCollection();
        Assert.False(weakEntity.TryGetTarget(out _));

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync();
        var concurrencyTag = Assert.Contains(entityId, snapshots).ConcurrencyTag;
        await SeedSnapshotAsync(broker, refreshedSnapshot, concurrencyTag);
        await broker.RefreshAsync();
    }

    [Fact]
    public async Task UpdateAsync_UpdatesSubscribedEntityWithoutRefreshAsync()
    {
        var entityId = new EntityId("44444444-4444-4444-4444-444444444444");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity"],
              "names": ["live-updated-entity"],
              "display-name": { "default": "Before Update" }
            }
            """);
        var broker = await CreateBrokerAsync();
        await SeedSnapshotAsync(broker, initialSnapshot);
        var entities = await broker.GetEntitiesAsync([entityId]);
        var entity = Assert.Single(entities);

        var changedEntityIds = new List<EntityId>();
        broker.Changed += (_, args) => changedEntityIds.AddRange(args.ChangedEntityIds);

        var updateResult = await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update title",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = entity.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = null,
                    },
                ],
            });

        var entityResult = Assert.Single(updateResult.EntityResults);
        Assert.Equal(UpdateState.Removed, entityResult.UpdateState);
        Assert.Null(entity.Data);
        Assert.Contains(entityId, changedEntityIds);
    }

    private static Task<EntityBroker> CreateBrokerAsync()
    {
        return EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<SubscribedEntityViewModel>> CreateCollectedEntityAsync(
        EntityBroker broker,
        EntityId entityId)
    {
        var entities = await broker.GetEntitiesAsync([entityId]);
        var entity = Assert.Single(entities);
        return new WeakReference<SubscribedEntityViewModel>(entity);
    }

    private static void ForceGarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task SeedSnapshotAsync(
        EntityBroker broker,
        EntitySnapshot snapshot,
        ConcurrencyTag? concurrencyTag = null)
    {
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Seed entity broker test snapshot.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = snapshot.EntityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = snapshot.Data?.Clone(),
                    },
                ],
            });
    }

    private static EntitySnapshot CreateSnapshot(
        EntityId entityId,
        Timestamp modifiedTime,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return new EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = new ConcurrencyTag(modifiedTime.ChangeId),
            ModifiedTime = modifiedTime,
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }
}
