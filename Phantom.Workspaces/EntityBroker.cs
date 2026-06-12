using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public sealed class EntityBroker
{
    private readonly EntityRepository entityRepository;
    private readonly object gate = new();
    private readonly Dictionary<EntityId, WeakReference<SubscribedEntityViewModel>> subscribedEntitiesById = new();
    private readonly List<WeakReference<SubscribedGet>> subscribedGets = new();

    public EntityBroker(
        EntityRepository entityRepository)
    {
        this.entityRepository = entityRepository;
    }

    public EntityRepository EntityRepository => this.entityRepository;

    public static async Task<EntityBroker> CreateInitializedAsync(
        RepositorySource repositorySource,
        CancellationToken cancellationToken = default)
    {
        var repository = await EntityRepository.CreateAsync(repositorySource);
        cancellationToken.ThrowIfCancellationRequested();

        var broker = new EntityBroker(repository);
        await broker.InitializeAsync(cancellationToken);
        return broker;
    }

    public event EventHandler<EntityBrokerChangedEventArgs>? Changed;

    public Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyCollection<SubscribedEntityViewModel>> GetEntitiesAsync(
        IReadOnlyCollection<EntityId> entityIds,
        CancellationToken cancellationToken = default)
    {
        var requests = entityIds.Distinct().Select(static entityId => new GetEntityRequest
        {
            EntityId = entityId,
        }).ToArray();
        return await this.GetEntitiesAsync(requests, cancellationToken);
    }

    public async Task<IReadOnlyCollection<SubscribedEntityViewModel>> GetEntitiesAsync(
        IReadOnlyCollection<GetEntityRequest> entityRequests,
        CancellationToken cancellationToken = default)
    {
        var loadedSnapshots = await this.LoadSnapshotsAsync(entityRequests, cancellationToken);
        var entities = new List<SubscribedEntityViewModel>();

        lock (this.gate)
        {
            foreach (var snapshot in loadedSnapshots.Values)
            {
                var entity = this.GetOrCreateSubscribedEntity(snapshot);
                entities.Add(entity);
            }
        }

        return entities;
    }

    public async Task<SubscribedGet> SubscribeGetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        var subscribedGet = new SubscribedGet(this, request);
        lock (this.gate)
        {
            this.subscribedGets.Add(new WeakReference<SubscribedGet>(subscribedGet));
        }

        await subscribedGet.RefreshAsync(cancellationToken);
        return subscribedGet;
    }

    public async Task<IReadOnlyDictionary<EntityId, EntitySnapshot>> ExportEntitySnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        return await this.entityRepository.ExportEntitySnapshotsAsync(cancellationToken);
    }

    public bool TryGetReferencedEntity(
        JsonElement element,
        string propertyName,
        out SubscribedEntityViewModel? entity)
    {
        entity = null;

        var reference = element.TryReadEntityReference(propertyName);
        if (reference is null)
        {
            return false;
        }

        lock (this.gate)
        {
            var snapshot = this.ResolveEntityReference(reference.Value);
            if (snapshot?.EntityId is EntityId entityId)
            {
                entity = this.GetOrCreateSubscribedEntity(snapshot);
                return true;
            }
        }

        return false;
    }

    public async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var updateResult = await this.entityRepository.DataAccessLayer.UpdateAsync(request, cancellationToken);

        var changedEntityIds = new HashSet<EntityId>();
        lock (this.gate)
        {
            foreach (var entityResult in updateResult.EntityResults)
            {
                var entityId = entityResult.RequestedEntityId;
                if (!this.subscribedEntitiesById.TryGetValue(entityId, out var weakRef)
                    || !weakRef.TryGetTarget(out var entity))
                {
                    continue;
                }

                if (entityResult.CurrentEntity is EntitySnapshot currentEntity)
                {
                    entity.UpdateSnapshot(currentEntity);
                    changedEntityIds.Add(currentEntity.EntityId);
                }

                if (entityResult.UpdateState == UpdateState.Removed)
                {
                    entity.MarkDeleted();
                    changedEntityIds.Add(entityId);
                }
            }
        }

        if (changedEntityIds.Count > 0)
        {
            this.Changed?.Invoke(
                this,
                new EntityBrokerChangedEventArgs
                {
                    ChangedEntityIds = changedEntityIds.ToArray(),
                });
        }

        return updateResult;
    }

    public SubscribedEntityViewModel? GetEntity(EntityId entityId)
    {
        lock (this.gate)
        {
            if (this.subscribedEntitiesById.TryGetValue(entityId, out var weakRef)
                && weakRef.TryGetTarget(out var entity))
            {
                return entity;
            }
        }

        return null;
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var changedEntityIds = new HashSet<EntityId>();
        var liveEntities = this.GetLiveSubscribedEntities();
        if (liveEntities.Count > 0)
        {
            var snapshotsById = new Dictionary<EntityId, EntitySnapshot>();
            foreach (var entity in liveEntities)
            {
                snapshotsById[entity.EntityId] = entity.Snapshot;
            }

            var changedEntitiesResult = await this.entityRepository.DataAccessLayer.GetChangedEntitiesAsync(
                new GetChangedEntitiesRequest
                {
                    EntityIdTimestamps = snapshotsById.Select(
                        static pair => new EntityIdTimestamp(pair.Key, pair.Value.ModifiedTime)).ToArray(),
                },
                cancellationToken);

            foreach (var changedEntity in changedEntitiesResult.Entities)
            {
                if (changedEntity.Entity is not EntitySnapshot current)
                {
                    continue;
                }

                this.UpsertSubscribedEntity(current, changedEntityIds);
            }
        }

        await this.RefreshSubscribedGetsAsync(changedEntityIds, cancellationToken);
        if (changedEntityIds.Count == 0)
        {
            return;
        }

        this.Changed?.Invoke(
            this,
            new EntityBrokerChangedEventArgs
            {
                ChangedEntityIds = changedEntityIds.ToArray(),
            });
    }

    internal async Task<IReadOnlyCollection<SubscribedEntityViewModel>> GetSubscribedEntitiesForGetRequestAsync(
        GetRequest request,
        ISet<EntityId>? changedEntityIds = null,
        CancellationToken cancellationToken = default)
    {
        var getResult = await this.entityRepository.DataAccessLayer.GetAsync(request, cancellationToken);
        var snapshots = getResult.Batches.SelectMany(static batch => batch.Entities).ToArray();
        var entities = new List<SubscribedEntityViewModel>(snapshots.Length);

        lock (this.gate)
        {
            foreach (var snapshot in snapshots)
            {
                entities.Add(this.UpsertSubscribedEntity(snapshot, changedEntityIds));
            }
        }

        return entities;
    }

    private async Task<Dictionary<EntityId, EntitySnapshot>> LoadSnapshotsAsync(
        IReadOnlyCollection<GetEntityRequest> entityRequests,
        CancellationToken cancellationToken)
    {
        if (entityRequests.Count == 0)
        {
            return new Dictionary<EntityId, EntitySnapshot>();
        }

        var getResult = await this.entityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = entityRequests,
            },
            cancellationToken);

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .ToDictionary(static snapshot => snapshot.EntityId, static snapshot => snapshot);
    }

    private EntitySnapshot? ResolveEntityReference(
        EntityReference reference)
    {
        if (reference.EntityId is EntityId entityId
            && this.subscribedEntitiesById.TryGetValue(entityId, out var weakRef)
            && weakRef.TryGetTarget(out var entity))
        {
            return entity.Snapshot;
        }

        if (reference.EntityName is EntityName entityName)
        {
            foreach (var entityRef in this.subscribedEntitiesById.Values)
            {
                if (!entityRef.TryGetTarget(out var subscribedEntity))
                {
                    continue;
                }

                var snapshot = subscribedEntity.Snapshot;
                if (snapshot.Data is not JsonElement data)
                {
                    continue;
                }

                if (!TryReadNames(data, out var nameKeys))
                {
                    continue;
                }

                if (nameKeys.Contains(entityName))
                {
                    return snapshot;
                }
            }
        }

        return null;
    }

    private static bool TryReadNames(
        JsonElement entityData,
        out IReadOnlyCollection<EntityName> names)
    {
        var resolved = new List<EntityName>();
        if (!entityData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            names = resolved;
            return false;
        }

        foreach (var nameElement in namesElement.EnumerateArray())
        {
            var nameReference = nameElement.TryReadEntityReference();
            if (nameReference is not { EntityName: EntityName parsedName })
            {
                continue;
            }

            resolved.Add(parsedName);
        }

        names = resolved;
        return resolved.Count > 0;
    }

    private SubscribedEntityViewModel GetOrCreateSubscribedEntity(EntitySnapshot snapshot)
    {
        if (this.subscribedEntitiesById.TryGetValue(snapshot.EntityId, out var weakRef)
            && weakRef.TryGetTarget(out var entity))
        {
            return entity;
        }

        var newEntity = new SubscribedEntityViewModel(snapshot, this.DeleteSubscribedEntityAsync);
        this.subscribedEntitiesById[snapshot.EntityId] = new WeakReference<SubscribedEntityViewModel>(newEntity);
        return newEntity;
    }

    private SubscribedEntityViewModel UpsertSubscribedEntity(
        EntitySnapshot snapshot,
        ISet<EntityId>? changedEntityIds = null)
    {
        if (this.subscribedEntitiesById.TryGetValue(snapshot.EntityId, out var weakRef)
            && weakRef.TryGetTarget(out var existing))
        {
            if (existing.ModifiedTime != snapshot.ModifiedTime)
            {
                existing.UpdateSnapshot(snapshot);
                changedEntityIds?.Add(snapshot.EntityId);
            }

            return existing;
        }

        var created = new SubscribedEntityViewModel(snapshot, this.DeleteSubscribedEntityAsync);
        this.subscribedEntitiesById[snapshot.EntityId] = new WeakReference<SubscribedEntityViewModel>(created);
        changedEntityIds?.Add(snapshot.EntityId);
        return created;
    }

    private async Task DeleteSubscribedEntityAsync(
        SubscribedEntityViewModel entity)
    {
        await this.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Delete entity {entity.DisplayName} from entity card action.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entity.EntityId,
                        ConcurrencyTag = entity.Snapshot.ConcurrencyTag,
                        Data = null,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
    }

    private List<SubscribedEntityViewModel> GetLiveSubscribedEntities()
    {
        lock (this.gate)
        {
            var liveEntities = new List<SubscribedEntityViewModel>();
            var nextSubscribedEntitiesById = new Dictionary<EntityId, WeakReference<SubscribedEntityViewModel>>();

            foreach (var pair in this.subscribedEntitiesById)
            {
                if (pair.Value.TryGetTarget(out var entity))
                {
                    liveEntities.Add(entity);
                    nextSubscribedEntitiesById[pair.Key] = pair.Value;
                }
            }

            this.subscribedEntitiesById.Clear();
            foreach (var pair in nextSubscribedEntitiesById)
            {
                this.subscribedEntitiesById[pair.Key] = pair.Value;
            }

            return liveEntities;
        }
    }

    private async Task RefreshSubscribedGetsAsync(
        ISet<EntityId> changedEntityIds,
        CancellationToken cancellationToken)
    {
        List<SubscribedGet> liveSubscribedGets;
        lock (this.gate)
        {
            liveSubscribedGets = new List<SubscribedGet>();
            var nextReferences = new List<WeakReference<SubscribedGet>>();
            foreach (var reference in this.subscribedGets)
            {
                if (reference.TryGetTarget(out var subscribedGet))
                {
                    liveSubscribedGets.Add(subscribedGet);
                    nextReferences.Add(reference);
                }
            }

            this.subscribedGets.Clear();
            this.subscribedGets.AddRange(nextReferences);
        }

        foreach (var subscribedGet in liveSubscribedGets)
        {
            await subscribedGet.RefreshAsync(cancellationToken, changedEntityIds);
        }
    }
}

public sealed class EntityBrokerChangedEventArgs : EventArgs
{
    public required IReadOnlyCollection<EntityId> ChangedEntityIds { get; init; }
}

public sealed class SubscribedGet
{
    private readonly EntityBroker entityBroker;
    private readonly GetRequest request;

    internal SubscribedGet(
        EntityBroker entityBroker,
        GetRequest request)
    {
        this.entityBroker = entityBroker;
        this.request = request;
    }

    public ObservableCollection<SubscribedEntityViewModel> Results { get; } = [];

    internal async Task RefreshAsync(
        CancellationToken cancellationToken = default,
        ISet<EntityId>? changedEntityIds = null)
    {
        var nextResults = (await this.entityBroker.GetSubscribedEntitiesForGetRequestAsync(
            this.request,
            changedEntityIds,
            cancellationToken)).ToList();

        var nextIds = nextResults.Select(static result => result.EntityId).ToHashSet();
        for (var index = this.Results.Count - 1; index >= 0; index--)
        {
            if (!nextIds.Contains(this.Results[index].EntityId))
            {
                this.Results.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < nextResults.Count; targetIndex++)
        {
            var expected = nextResults[targetIndex];
            if (targetIndex < this.Results.Count
                && ReferenceEquals(this.Results[targetIndex], expected))
            {
                continue;
            }

            var existingIndex = this.Results.IndexOf(expected);
            if (existingIndex >= 0)
            {
                this.Results.Move(existingIndex, targetIndex);
                continue;
            }

            this.Results.Insert(targetIndex, expected);
        }
    }
}
