using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrokerTests
{
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
              "title": "Loaded"
            }
            """);

        var dataAccessLayer = new TrackingDataAccessLayer
        {
            CurrentSnapshotsById =
            {
                [entityId] = snapshot,
            },
        };
        var broker = CreateBroker(dataAccessLayer);

        var entities = await broker.GetEntitiesAsync([entityId]);

        Assert.Single(dataAccessLayer.GetRequests);
        var request = Assert.Single(dataAccessLayer.GetRequests[0].Entities);
        Assert.Equal(entityId, request.EntityId);

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
        var refreshedTimestamp = new Timestamp(DateTimeOffset.UtcNow, "2");

        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity"],
              "names": ["loaded-entity"],
              "title": "Loaded"
            }
            """);

        var refreshedSnapshot = CreateSnapshot(
            entityId,
            refreshedTimestamp,
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity"],
              "names": ["loaded-entity"],
              "title": "Updated"
            }
            """);

        var dataAccessLayer = new TrackingDataAccessLayer
        {
            CurrentSnapshotsById =
            {
                [entityId] = initialSnapshot,
            },
        };
        var broker = CreateBroker(dataAccessLayer);
        var entities = await broker.GetEntitiesAsync([entityId]);
        var entity = Assert.Single(entities);

        dataAccessLayer.CurrentSnapshotsById[entityId] = refreshedSnapshot;
        await broker.RefreshAsync();

        Assert.Single(dataAccessLayer.GetChangedEntitiesRequests);
        Assert.Equal("Updated", entity.DisplayName);
        Assert.Equal(refreshedTimestamp, entity.ModifiedTime);
        Assert.Contains("\"Updated\"", entity.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_SkipsCollectedSubscriptions()
    {
        var entityId = new EntityId("33333333-3333-3333-3333-333333333333");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var refreshedTimestamp = new Timestamp(DateTimeOffset.UtcNow, "2");

        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity"],
              "names": ["collected-entity"],
              "title": "Collected"
            }
            """);

        var refreshedSnapshot = CreateSnapshot(
            entityId,
            refreshedTimestamp,
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity"],
              "names": ["collected-entity"],
              "title": "Updated"
            }
            """);

        var dataAccessLayer = new TrackingDataAccessLayer
        {
            CurrentSnapshotsById =
            {
                [entityId] = initialSnapshot,
            },
        };
        var broker = CreateBroker(dataAccessLayer);
        var weakEntity = await CreateCollectedEntityAsync(broker, entityId);

        ForceGarbageCollection();
        Assert.False(weakEntity.TryGetTarget(out _));

        dataAccessLayer.CurrentSnapshotsById[entityId] = refreshedSnapshot;
        await broker.RefreshAsync();

        Assert.Empty(dataAccessLayer.GetChangedEntitiesRequests);
    }

    private static EntityBroker CreateBroker(
        IDataAccessLayer dataAccessLayer)
    {
        var repository = (EntityRepository)Activator.CreateInstance(
            typeof(EntityRepository),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                new RepositorySource(RepositorySourceType.Unknown, "(none)"),
                dataAccessLayer,
            ],
            culture: null)!;
        return new EntityBroker(repository);
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

    private sealed class TrackingDataAccessLayer : IDataAccessLayer
    {
        public List<GetRequest> GetRequests { get; } = [];

        public List<GetChangedEntitiesRequest> GetChangedEntitiesRequests { get; } = [];

        public Dictionary<EntityId, EntitySnapshot> CurrentSnapshotsById { get; } = new();

        public Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken = default)
        {
            this.GetRequests.Add(request);
            var entities = request.Entities
                .SelectMany(entityRequest =>
                    entityRequest.EntityId is EntityId entityId && this.CurrentSnapshotsById.TryGetValue(entityId, out var snapshot)
                        ? new[] { snapshot }
                        : Array.Empty<EntitySnapshot>())
                .ToArray();

            return Task.FromResult(
                new GetResult
                {
                    Batches =
                    [
                        new TimestampedEntityBatch
                        {
                            Timestamp = null,
                            Entities = entities,
                        },
                    ],
                });
        }

        public Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken = default)
        {
            this.GetChangedEntitiesRequests.Add(request);

            var changedEntities = new List<ChangedEntitySnapshot>();
            foreach (var entityRequest in request.EntityIdTimestamps)
            {
                if (!this.CurrentSnapshotsById.TryGetValue(entityRequest.EntityId, out var currentSnapshot))
                {
                    continue;
                }

                if (currentSnapshot.ModifiedTime.DateTime > entityRequest.Timestamp.DateTime
                    || (currentSnapshot.ModifiedTime.DateTime == entityRequest.Timestamp.DateTime
                        && string.CompareOrdinal(currentSnapshot.ModifiedTime.ChangeId, entityRequest.Timestamp.ChangeId) > 0))
                {
                    changedEntities.Add(new ChangedEntitySnapshot { Entity = currentSnapshot });
                }
            }

            return Task.FromResult(
                new GetChangedEntitiesResult
                {
                    Entities = changedEntities,
                });
        }
    }
}
