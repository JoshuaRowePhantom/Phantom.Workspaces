using System;
using System.Collections.Generic;
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
                if (entityResult.CurrentEntity is not EntitySnapshot currentEntity)
                {
                    continue;
                }

                if (this.subscribedEntitiesById.TryGetValue(currentEntity.EntityId, out var weakRef)
                    && weakRef.TryGetTarget(out var entity))
                {
                    entity.UpdateSnapshot(currentEntity);
                    changedEntityIds.Add(currentEntity.EntityId);
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
        var liveEntities = this.GetLiveSubscribedEntities();
        if (liveEntities.Count == 0)
        {
            return;
        }

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

        if (changedEntitiesResult.Entities.Count == 0)
        {
            return;
        }

        var changedEntityIds = new HashSet<EntityId>();
        lock (this.gate)
        {
            foreach (var changedEntity in changedEntitiesResult.Entities)
            {
                if (changedEntity.Entity is not EntitySnapshot current)
                {
                    continue;
                }

                if (this.subscribedEntitiesById.TryGetValue(current.EntityId, out var weakRef)
                    && weakRef.TryGetTarget(out var entity))
                {
                    entity.UpdateSnapshot(current);
                    changedEntityIds.Add(current.EntityId);
                }
            }
        }

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

        var newEntity = new SubscribedEntityViewModel(snapshot);
        this.subscribedEntitiesById[snapshot.EntityId] = new WeakReference<SubscribedEntityViewModel>(newEntity);
        return newEntity;
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
}

public sealed class EntityBrokerChangedEventArgs : EventArgs
{
    public required IReadOnlyCollection<EntityId> ChangedEntityIds { get; init; }
}
