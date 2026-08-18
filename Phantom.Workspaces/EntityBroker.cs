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
    private readonly Dictionary<string, WeakReference<SubscribedGet>> subscribedGets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<SubscribedQuery>> subscribedQueries = new(StringComparer.Ordinal);
    private readonly EntityBrokerQuerySatisfier querySatisfier;
    private Action<Action> uiMarshal = static action => action();

    public EntityBroker(
        EntityRepository entityRepository)
    {
        this.entityRepository = entityRepository;
        this.querySatisfier = new EntityBrokerQuerySatisfier(
            new DataAccessLayerQueryExecutor(() => this.entityRepository.DataAccessLayer),
            this.ResolveCachedSnapshot);
    }

    private EntitySnapshot? ResolveCachedSnapshot(EntityId entityId)
    {
        lock (this.gate)
        {
            if (this.subscribedEntitiesById.TryGetValue(entityId, out var weakRef)
                && weakRef.TryGetTarget(out var entity))
            {
                return entity.Snapshot;
            }
        }

        return null;
    }

    public EntityRepository EntityRepository => this.entityRepository;

    /// <summary>
    /// The interest-type catalog used by <see cref="ToggleInterestAsync"/> to resolve an interest-type
    /// name (received via the entity-badge callback) into the fully-declared
    /// <see cref="InterestTypeDefinition"/> whose <c>target-participant</c> and <c>applies-to</c>
    /// participants describe the relationship shape to write. Set by the composition root
    /// (<see cref="ViewModels.MainWindowViewModel"/>) after the catalog is initialised.
    /// </summary>
    public InterestCatalog? InterestCatalog { get; set; }

    /// <summary>
    /// Marshals UI-bound mutations onto the UI thread. <see cref="UpdateAsync"/> runs the write and the
    /// subsequent subscription refresh off the UI thread (thread pool), so mutations to the subscribed
    /// result collections (<see cref="SubscribedResults.Merge"/>) and to <see cref="SubscribedEntityViewModel"/>
    /// properties bound to controls must be marshaled so the live rebind does not cross-thread the UI.
    /// Defaults to inline execution; the application wires this to the Avalonia dispatcher.
    /// </summary>
    internal Action<Action> UiMarshal
    {
        get => this.uiMarshal;
        set => this.uiMarshal = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal void RunOnUiThread(Action action) => this.uiMarshal(action);

    private readonly record struct PendingSnapshotUpdate(SubscribedEntityViewModel Entity, EntitySnapshot Snapshot);

    private void ApplyPendingSnapshotUpdates(List<PendingSnapshotUpdate> pendingUpdates)
    {
        if (pendingUpdates.Count == 0)
        {
            return;
        }

        this.uiMarshal(() =>
        {
            foreach (var pendingUpdate in pendingUpdates)
            {
                pendingUpdate.Entity.UpdateSnapshot(pendingUpdate.Snapshot);
            }
        });
    }

    public static async Task<EntityBroker> CreateInitializedAsync(
        RepositorySource repositorySource,
        CancellationToken cancellationToken = default,
        string? userComputerProfileOverride = null)
    {
        var repository = await EntityRepository.CreateAsync(repositorySource, userComputerProfileOverride);
        cancellationToken.ThrowIfCancellationRequested();

        var broker = new EntityBroker(repository);
        await broker.InitializeAsync(cancellationToken);
        broker.InterestCatalog = await InterestCatalog.CreateAsync(broker, cancellationToken);
        return broker;
    }

    public event EventHandler<EntityBrokerChangedEventArgs>? Changed;

    public bool TryGetEntity(EntityId entityId, out SubscribedEntityViewModel? entity)
    {
        lock (this.gate)
        {
            if (this.subscribedEntitiesById.TryGetValue(entityId, out var reference)
                && reference.TryGetTarget(out entity))
            {
                return true;
            }
        }

        entity = null;
        return false;
    }

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
        var key = JsonSerializer.Serialize(request);

        lock (this.gate)
        {
            if (this.subscribedGets.TryGetValue(key, out var existingRef)
                && existingRef.TryGetTarget(out var existing))
            {
                return existing;
            }
        }

        var subscribedGet = new SubscribedGet(this, request);
        lock (this.gate)
        {
            if (this.subscribedGets.TryGetValue(key, out var existingRef)
                && existingRef.TryGetTarget(out var existing))
            {
                return existing;
            }

            this.subscribedGets[key] = new WeakReference<SubscribedGet>(subscribedGet);
        }

        await subscribedGet.RefreshAsync(cancellationToken);
        return subscribedGet;
    }

    /// <summary>
    /// Subscribes to a <see cref="QueryRequest"/>, returning a live <see cref="SubscribedQuery"/> whose
    /// results are refreshed as the broker observes changes (mirrors <see cref="SubscribeGetAsync"/> for
    /// query-driven views such as inbox and workstreams).
    /// </summary>
    public async Task<SubscribedQuery> SubscribeQueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = JsonSerializer.Serialize(request);

        lock (this.gate)
        {
            if (this.subscribedQueries.TryGetValue(key, out var existingRef)
                && existingRef.TryGetTarget(out var existing))
            {
                return existing;
            }
        }

        var subscribedQuery = new SubscribedQuery(this, request);
        lock (this.gate)
        {
            if (this.subscribedQueries.TryGetValue(key, out var existingRef)
                && existingRef.TryGetTarget(out var existing))
            {
                return existing;
            }

            this.subscribedQueries[key] = new WeakReference<SubscribedQuery>(subscribedQuery);
        }

        await subscribedQuery.RefreshAsync(cancellationToken);
        return subscribedQuery;
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
        // Run this on the default scheduler to avoid blocking the UI thread with a potentially long-running operation
        var updateResult = await Task.Run(() => this.entityRepository.DataAccessLayer.UpdateAsync(request, cancellationToken));

        var changedEntityIds = new HashSet<EntityId>();
        var uiMutations = new List<Action>();
        lock (this.gate)
        {
            foreach (var entityResult in updateResult.EntityResults)
            {
                var entityId = entityResult.RequestedEntityId;

                // Track all changed entities for subscription refresh,
                // not just those that are currently subscribed
                if (entityResult.UpdateState == UpdateState.Failed)
                {
                    continue;
                }

                changedEntityIds.Add(entityId);

                // A relationship write (for example toggling an interest badge) creates or deletes a
                // separate relationship entity and never bumps its participants' ModifiedTime. Expand
                // the changed set to the relationship's participants so the queries that return those
                // participants (and their relationship-derived data) are treated as affected. The new
                // participants come from the write's resulting data; for a removal the resulting data
                // is gone, so fall back to the previously-known snapshot of the relationship entity.
                AddRelationshipParticipantIds(entityResult.CurrentEntity?.Data, changedEntityIds);

                // Update subscribed entities if they exist
                if (this.subscribedEntitiesById.TryGetValue(entityId, out var weakRef)
                    && weakRef.TryGetTarget(out var entity))
                {
                    AddRelationshipParticipantIds(entity.Snapshot.Data, changedEntityIds);

                    // The snapshot/deletion mutations raise PropertyChanged on controls bound to the
                    // entity, so they are deferred and applied on the UI thread outside the gate.
                    if (entityResult.CurrentEntity is EntitySnapshot currentEntity)
                    {
                        uiMutations.Add(() => entity.UpdateSnapshot(currentEntity));
                    }

                    if (entityResult.UpdateState == UpdateState.Removed)
                    {
                        uiMutations.Add(() => entity.MarkDeleted());
                    }
                }
            }
        }

        if (uiMutations.Count > 0)
        {
            this.uiMarshal(() =>
            {
                foreach (var mutation in uiMutations)
                {
                    mutation();
                }
            });
        }

        if (changedEntityIds.Count > 0)
        {
            var getsChanged = await this.RefreshSubscribedGetsAsync(changedEntityIds, periodic: false, cancellationToken).ConfigureAwait(false);
            var queriesChanged = await this.RefreshSubscribedQueriesAsync(changedEntityIds, periodic: false, cancellationToken).ConfigureAwait(false);
            this.Changed?.Invoke(
                this,
                new EntityBrokerChangedEventArgs
                {
                    ChangedEntityIds = changedEntityIds.ToArray(),
                    HasQueryMembershipChanges = getsChanged || queriesChanged,
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

            // #1328: route through the satisfier as a periodic (tick-driven) read so it is throttled
            // one-at-a-time and can subsume the per-subscription refreshes below.
            var changedEntitiesResult = await this.querySatisfier.SatisfyOrEnqueuePeriodicGetChangedAsync(
                new GetChangedEntitiesRequest
                {
                    EntityIdTimestamps = snapshotsById.Select(
                        static pair => new EntityIdTimestamp(pair.Key, pair.Value.ModifiedTime)).ToArray(),
                },
                cancellationToken);

            var pendingUpdates = new List<PendingSnapshotUpdate>();
            lock (this.gate)
            {
                foreach (var changedEntity in changedEntitiesResult.Entities)
                {
                    if (changedEntity.Entity is not EntitySnapshot current)
                    {
                        continue;
                    }

                    this.UpsertSubscribedEntity(current, changedEntityIds, pendingUpdates);
                }
            }

            this.ApplyPendingSnapshotUpdates(pendingUpdates);
        }

        var getsChanged = await this.RefreshSubscribedGetsAsync(changedEntityIds, periodic: true, cancellationToken);
        var queriesChanged = await this.RefreshSubscribedQueriesAsync(changedEntityIds, periodic: true, cancellationToken);
        if (changedEntityIds.Count == 0)
        {
            return;
        }

        this.Changed?.Invoke(
            this,
            new EntityBrokerChangedEventArgs
            {
                ChangedEntityIds = changedEntityIds.ToArray(),
                HasQueryMembershipChanges = getsChanged || queriesChanged,
            });
    }

    internal async Task<IReadOnlyCollection<SubscribedEntityViewModel>> GetSubscribedEntitiesForGetRequestAsync(
        GetRequest request,
        ISet<EntityId>? changedEntityIds = null,
        bool periodic = false,
        CancellationToken cancellationToken = default)
    {
        // #1328: route background reads through the satisfier so identical/subsumed calls coalesce.
        var getResult = periodic
            ? await this.querySatisfier.SatisfyOrEnqueuePeriodicGetAsync(request, cancellationToken)
            : await this.querySatisfier.SatisfyOrIssueOnDemandGetAsync(request, cancellationToken);
        var snapshots = getResult.Batches.SelectMany(static batch => batch.Entities).ToArray();
        var entities = new List<SubscribedEntityViewModel>(snapshots.Length);

        var pendingUpdates = new List<PendingSnapshotUpdate>();
        lock (this.gate)
        {
            foreach (var snapshot in snapshots)
            {
                entities.Add(this.UpsertSubscribedEntity(snapshot, changedEntityIds, pendingUpdates));
            }
        }

        this.ApplyPendingSnapshotUpdates(pendingUpdates);

        return entities;
    }

    internal async Task<IReadOnlyCollection<SubscribedEntityViewModel>> GetSubscribedEntitiesForQueryRequestAsync(
        QueryRequest request,
        ISet<EntityId>? changedEntityIds = null,
        bool periodic = false,
        CancellationToken cancellationToken = default)
    {
        // #1328: route background reads through the satisfier so identical/subsumed calls coalesce.
        var queryResult = periodic
            ? await this.querySatisfier.SatisfyOrEnqueuePeriodicQueryAsync(request, cancellationToken)
            : await this.querySatisfier.SatisfyOrIssueOnDemandQueryAsync(request, cancellationToken);
        var snapshots = queryResult.Batches.SelectMany(static batch => batch.Entities).ToArray();
        var entities = new List<SubscribedEntityViewModel>(snapshots.Length);

        var pendingUpdates = new List<PendingSnapshotUpdate>();
        lock (this.gate)
        {
            foreach (var snapshot in snapshots)
            {
                entities.Add(this.UpsertSubscribedEntity(snapshot, changedEntityIds, pendingUpdates));
            }
        }

        this.ApplyPendingSnapshotUpdates(pendingUpdates);

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

        // #1328: on-demand read (invoked by GetEntitiesAsync / subscription warm-up).
        var getResult = await this.querySatisfier.SatisfyOrIssueOnDemandGetAsync(
            new GetRequest
            {
                Entities = entityRequests,
            },
            cancellationToken);

        var result = new Dictionary<EntityId, EntitySnapshot>();
        foreach (var snapshot in getResult.Batches.SelectMany(static b => b.Entities))
        {
            result[snapshot.EntityId] = snapshot;
        }
        return result;
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

        var newEntity = new SubscribedEntityViewModel(
            snapshot,
            this.DeleteSubscribedEntityAsync,
            this.ToggleInterestAsync,
            this.SaveSubscribedEntityAsync);
        this.subscribedEntitiesById[snapshot.EntityId] = new WeakReference<SubscribedEntityViewModel>(newEntity);
        return newEntity;
    }

    private SubscribedEntityViewModel UpsertSubscribedEntity(
        EntitySnapshot snapshot,
        ISet<EntityId>? changedEntityIds = null,
        List<PendingSnapshotUpdate>? pendingUpdates = null)
    {
        if (this.subscribedEntitiesById.TryGetValue(snapshot.EntityId, out var weakRef)
            && weakRef.TryGetTarget(out var existing))
        {
            // Apply the re-queried snapshot when its content actually changed, not only when the
            // entity's own ModifiedTime moved. A relationship-only update (for example toggling an
            // interest) leaves the participant's ModifiedTime untouched but changes the relationships
            // returned alongside it, so a ModifiedTime-only gate would silently drop the fresh data.
            var snapshotChanged =
                existing.ModifiedTime != snapshot.ModifiedTime
                || !RelationshipsEquivalent(existing.Snapshot.Relationships, snapshot.Relationships);
            if (snapshotChanged)
            {
                // Defer the snapshot mutation (it raises PropertyChanged on bound controls) so it can
                // be marshaled to the UI thread outside the gate; fall back to inline application when
                // no pending list is supplied.
                if (pendingUpdates is not null)
                {
                    pendingUpdates.Add(new PendingSnapshotUpdate(existing, snapshot));
                }
                else
                {
                    this.uiMarshal(() => existing.UpdateSnapshot(snapshot));
                }

                changedEntityIds?.Add(snapshot.EntityId);
            }

            return existing;
        }

        var created = new SubscribedEntityViewModel(
            snapshot,
            this.DeleteSubscribedEntityAsync,
            this.ToggleInterestAsync,
            this.SaveSubscribedEntityAsync);
        this.subscribedEntitiesById[snapshot.EntityId] = new WeakReference<SubscribedEntityViewModel>(created);
        changedEntityIds?.Add(snapshot.EntityId);
        return created;
    }

    private static void AddRelationshipParticipantIds(
        JsonElement? data,
        ISet<EntityId> changedEntityIds)
    {
        if (data is JsonElement element
            && RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(element, out var participantIds))
        {
            foreach (var participantId in participantIds)
            {
                changedEntityIds.Add(participantId);
            }
        }
    }

    private static bool RelationshipsEquivalent(
        IReadOnlyCollection<EntitySnapshot> current,
        IReadOnlyCollection<EntitySnapshot> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        var nextById = next.ToDictionary(static relationship => relationship.EntityId);
        foreach (var relationship in current)
        {
            if (!nextById.TryGetValue(relationship.EntityId, out var other)
                || other.ModifiedTime != relationship.ModifiedTime
                || other.ConcurrencyTag != relationship.ConcurrencyTag)
            {
                return false;
            }
        }

        return true;
    }

    private async Task ToggleInterestAsync(
        SubscribedEntityViewModel entity,
        string interestTypeName)
    {
        if (this.InterestCatalog is not { } catalog)
        {
            throw new InvalidOperationException(
                $"Cannot toggle interest '{interestTypeName}': no interest catalog is attached to the entity broker.");
        }

        var definition = catalog.InterestTypes.FirstOrDefault(
            type => string.Equals(type.Name, interestTypeName, StringComparison.Ordinal));
        if (definition is null)
        {
            throw new InvalidOperationException(
                $"Cannot toggle interest '{interestTypeName}': no interest-type definition with that name is registered in the catalog.");
        }

        await InterestToggle.ToggleAsync(this, entity.Snapshot, definition);
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

    private async Task SaveSubscribedEntityAsync(
        SubscribedEntityViewModel entity,
        System.Text.Json.JsonElement data)
    {
        await this.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Edit entity {entity.DisplayName} from entity card action.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entity.EntityId,
                        ConcurrencyTag = entity.Snapshot.ConcurrencyTag,
                        Data = data,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
    }

    internal int ActiveSubscribedGetCount
    {
        get
        {
            lock (this.gate)
                return this.subscribedGets.Count(r => r.Value.TryGetTarget(out _));
        }
    }

    internal int ActiveSubscribedQueryCount
    {
        get
        {
            lock (this.gate)
                return this.subscribedQueries.Count(r => r.Value.TryGetTarget(out _));
        }
    }

    public async Task<IReadOnlyCollection<SubscribedEntityViewModel>> GetEntitiesAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return await this.GetSubscribedEntitiesForQueryRequestAsync(request, null, periodic: false, cancellationToken);
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

    private async Task<bool> RefreshSubscribedGetsAsync(
        ISet<EntityId> changedEntityIds,
        bool periodic,
        CancellationToken cancellationToken)
    {
        List<SubscribedGet> liveSubscribedGets;
        lock (this.gate)
        {
            liveSubscribedGets = new List<SubscribedGet>();
            var deadKeys = new List<string>();
            foreach (var (key, reference) in this.subscribedGets)
            {
                if (reference.TryGetTarget(out var subscribedGet))
                {
                    liveSubscribedGets.Add(subscribedGet);
                }
                else
                {
                    deadKeys.Add(key);
                }
            }

            foreach (var key in deadKeys)
            {
                this.subscribedGets.Remove(key);
            }
        }

        bool anyMembershipChanged = false;
        foreach (var subscribedGet in liveSubscribedGets)
        {
            if (await subscribedGet.RefreshAsync(cancellationToken, changedEntityIds, periodic))
            {
                anyMembershipChanged = true;
            }
        }

        return anyMembershipChanged;
    }

    private async Task<bool> RefreshSubscribedQueriesAsync(
        ISet<EntityId> changedEntityIds,
        bool periodic,
        CancellationToken cancellationToken)
    {
        List<SubscribedQuery> liveSubscribedQueries;
        lock (this.gate)
        {
            liveSubscribedQueries = new List<SubscribedQuery>();
            var deadKeys = new List<string>();
            foreach (var (key, reference) in this.subscribedQueries)
            {
                if (reference.TryGetTarget(out var subscribedQuery))
                {
                    liveSubscribedQueries.Add(subscribedQuery);
                }
                else
                {
                    deadKeys.Add(key);
                }
            }

            foreach (var key in deadKeys)
            {
                this.subscribedQueries.Remove(key);
            }
        }

        bool anyMembershipChanged = false;
        foreach (var subscribedQuery in liveSubscribedQueries)
        {
            if (await subscribedQuery.RefreshAsync(cancellationToken, changedEntityIds, periodic))
            {
                anyMembershipChanged = true;
            }
        }

        return anyMembershipChanged;
    }
}

public sealed class EntityBrokerChangedEventArgs : EventArgs
{
    public required IReadOnlyCollection<EntityId> ChangedEntityIds { get; init; }

    /// <summary>
    /// True when at least one subscribed <see cref="SubscribedGet"/> or <see cref="SubscribedQuery"/>
    /// result set gained or lost members as a result of this change batch. False when the change
    /// was purely a data update to entities already present in all subscriptions.
    /// </summary>
    public bool HasQueryMembershipChanges { get; init; }
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

    internal async Task<bool> RefreshAsync(
        CancellationToken cancellationToken = default,
        ISet<EntityId>? changedEntityIds = null,
        bool periodic = false)
    {
        var nextResults = (await this.entityBroker.GetSubscribedEntitiesForGetRequestAsync(
            this.request,
            changedEntityIds,
            periodic,
            cancellationToken)).ToList();

        // The result collection is bound to the UI; marshal the merge so it does not cross-thread the
        // bound controls when the refresh runs off the UI thread.
        var membershipChanged = false;
        this.entityBroker.RunOnUiThread(() => membershipChanged = SubscribedResults.Merge(this.Results, nextResults));
        return membershipChanged;
    }
}

/// <summary>
/// A live subscription to a <see cref="QueryRequest"/>. Its <see cref="Results"/> are kept in sync with
/// the matching entities as the broker observes changes (the query-driven counterpart to
/// <see cref="SubscribedGet"/>).
/// </summary>
public sealed class SubscribedQuery
{
    private readonly EntityBroker entityBroker;
    private readonly QueryRequest request;

    internal SubscribedQuery(
        EntityBroker entityBroker,
        QueryRequest request)
    {
        this.entityBroker = entityBroker;
        this.request = request;
    }

    public ObservableCollection<SubscribedEntityViewModel> Results { get; } = [];

    internal async Task<bool> RefreshAsync(
        CancellationToken cancellationToken = default,
        ISet<EntityId>? changedEntityIds = null,
        bool periodic = false)
    {
        var nextResults = (await this.entityBroker.GetSubscribedEntitiesForQueryRequestAsync(
            this.request,
            changedEntityIds,
            periodic,
            cancellationToken)).ToList();

        // The result collection is bound to the UI; marshal the merge so it does not cross-thread the
        // bound controls when the refresh runs off the UI thread.
        var membershipChanged = false;
        this.entityBroker.RunOnUiThread(() => membershipChanged = SubscribedResults.Merge(this.Results, nextResults));
        return membershipChanged;
    }
}

/// <summary>Shared incremental merge of a subscribed result collection toward the next ordered result set.</summary>
internal static class SubscribedResults
{
    public static bool Merge(
        ObservableCollection<SubscribedEntityViewModel> results,
        IReadOnlyList<SubscribedEntityViewModel> nextResults)
    {
        bool membershipChanged = false;
        var nextIds = nextResults.Select(static result => result.EntityId).ToHashSet();
        for (var index = results.Count - 1; index >= 0; index--)
        {
            if (!nextIds.Contains(results[index].EntityId))
            {
                results.RemoveAt(index);
                membershipChanged = true;
            }
        }

        for (var targetIndex = 0; targetIndex < nextResults.Count; targetIndex++)
        {
            var expected = nextResults[targetIndex];
            if (targetIndex < results.Count
                && ReferenceEquals(results[targetIndex], expected))
            {
                continue;
            }

            var existingIndex = results.IndexOf(expected);
            if (existingIndex >= 0)
            {
                results.Move(existingIndex, targetIndex);
                continue;
            }

            results.Insert(targetIndex, expected);
            membershipChanged = true;
        }

        return membershipChanged;
    }
}
