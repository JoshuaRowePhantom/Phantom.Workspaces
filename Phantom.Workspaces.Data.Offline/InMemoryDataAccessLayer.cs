using System.Collections.Generic;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Offline;

public sealed class InMemoryDataAccessLayer : IDataAccessLayer
{
    private State currentState = State.CreateInitial();

    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ReadState().ExportAsync(request, cancellationToken);
    }

    public Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ReadState().GetAsync(request, cancellationToken);
    }

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ReadState().GetChangedEntitiesAsync(request, cancellationToken);
    }

    public Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ReadState().GetHistoryAsync(request, cancellationToken);
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.ReadState().QueryAsync(request, cancellationToken);
    }

    public Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var state = this.ReadState();
            var updateOutcome = state.UpdateAsync(request, cancellationToken);

            if (ReferenceEquals(
                Interlocked.CompareExchange(ref this.currentState, updateOutcome.NextState, state),
                state))
            {
                return Task.FromResult(updateOutcome.UpdateResult);
            }
        }
    }

    private State ReadState()
    {
        return Volatile.Read(ref this.currentState);
    }

    private sealed record EntityVersion(
        Timestamp Timestamp,
        ConcurrencyTag ConcurrencyTag,
        JsonNode? Data);

    private sealed record UpdateOutcome(
        State NextState,
        UpdateResult UpdateResult);

    private sealed class State
    {
        private readonly StateSnapshot snapshot;
        private readonly long nextSequenceNumber;

        private State(
            StateSnapshot snapshot,
            long nextSequenceNumber)
        {
            this.snapshot = snapshot;
            this.nextSequenceNumber = nextSequenceNumber;
        }

        public static State CreateInitial()
        {
            return new State(
                new StateSnapshot(new Dictionary<EntityId, List<EntityVersion>>()),
                0);
        }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken)
        {
            return this.snapshot.ExportAsync(request, cancellationToken);
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken)
        {
            return this.snapshot.GetAsync(request, cancellationToken);
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken)
        {
            return this.snapshot.GetChangedEntitiesAsync(request, cancellationToken);
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken)
        {
            return this.snapshot.GetHistoryAsync(request, cancellationToken);
        }

        public Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken)
        {
            return this.snapshot.QueryAsync(request, cancellationToken);
        }

        public UpdateOutcome UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextEntities = this.snapshot.CloneEntities();
            var updateResults = new List<EntityUpdateResult>();
            var nextSequenceNumber = this.nextSequenceNumber;

            foreach (var change in request.Changes)
            {
                var entityId = change.EntityId ?? GetEntityId(change.Data);
                if (entityId is null)
                {
                    updateResults.Add(
                        new EntityUpdateResult(
                            UpdateState.Failed,
                            default,
                            default,
                            null,
                            ConcurrencyMatchState.NotMatched,
                            new[]
                            {
                                new UpdateError(
                                    "Entity data must include an entity-id.",
                                    null),
                            }));
                    continue;
                }

                nextEntities.TryGetValue(entityId.Value, out var versions);
                versions ??= new List<EntityVersion>();
                var currentVersion = versions.Count > 0 ? versions[^1] : null;

                if (change.ConcurrencyTag is not null
                    && currentVersion is not null
                    && currentVersion.ConcurrencyTag != change.ConcurrencyTag.Value)
                {
                    updateResults.Add(
                        new EntityUpdateResult(
                            UpdateState.Failed,
                            entityId.Value,
                            entityId.Value,
                            currentVersion.ConcurrencyTag,
                            ConcurrencyMatchState.NotMatched,
                            new[]
                            {
                                new UpdateError(
                                    "Concurrency tag does not match.",
                                    entityId.Value),
                            }));
                    continue;
                }

                nextSequenceNumber++;
                var timestamp = new Timestamp(
                    DateTimeOffset.UtcNow,
                    nextSequenceNumber.ToString());
                var concurrencyTag = new ConcurrencyTag(Guid.NewGuid().ToString("D"));

                versions.Add(new EntityVersion(timestamp, concurrencyTag, change.Data?.DeepClone()));
                nextEntities[entityId.Value] = versions;

                updateResults.Add(
                    new EntityUpdateResult(
                        change.Data is null ? UpdateState.Removed : currentVersion is null ? UpdateState.Added : UpdateState.Updated,
                        entityId.Value,
                        entityId.Value,
                        concurrencyTag,
                        ConcurrencyMatchState.Matched,
                        Array.Empty<UpdateError>()));
            }

            return new UpdateOutcome(
                new State(
                    new StateSnapshot(nextEntities),
                    nextSequenceNumber),
                new UpdateResult(updateResults));
        }

        private static EntityId? GetEntityId(
            JsonNode? data)
        {
            if (data is not JsonObject jsonObject)
            {
                return null;
            }

            if (!jsonObject.TryGetPropertyValue("entity-id", out var entityIdNode)
                || entityIdNode is not JsonValue entityIdValue
                || !entityIdValue.TryGetValue<string>(out var entityIdText)
                || !Guid.TryParse(entityIdText, out var entityGuid))
            {
                return null;
            }

            return new EntityId(entityGuid);
        }
    }

    private sealed class StateSnapshot
    {
        private readonly Dictionary<EntityId, List<EntityVersion>> entities;

        public StateSnapshot(
            Dictionary<EntityId, List<EntityVersion>> entities)
        {
            this.entities = entities;
        }

        public Dictionary<EntityId, List<EntityVersion>> CloneEntities()
        {
            return this.entities.ToDictionary(
                entry => entry.Key,
                entry => new List<EntityVersion>(entry.Value));
        }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changeBatches = new List<ExportChangeBatch>();

            foreach (var entityEntry in this.entities)
            {
                foreach (var version in entityEntry.Value)
                {
                    if (request.SnapshotTime is not null
                        && CompareTimestamp(version.Timestamp, request.SnapshotTime.Value) <= 0)
                    {
                        continue;
                    }

                    changeBatches.Add(
                        new ExportChangeBatch(
                            version.Timestamp,
                            new[]
                            {
                                new QueryEntitySnapshot(
                                    entityEntry.Key,
                                    version.ConcurrencyTag,
                                    version.Timestamp,
                                    null,
                                    version.Data?.DeepClone(),
                                    Array.Empty<QueryClauseIdentifier>(),
                                    Array.Empty<FullTextQueryScore>()),
                            }));
                }
            }

            changeBatches.Sort(
                static (left, right) => CompareTimestamp(left.ChangeTime, right.ChangeTime));

            var finalSnapshotTime = this.entities.Count == 0
                ? new Timestamp(DateTimeOffset.UnixEpoch, "0")
                : this.entities.Values
                    .SelectMany(static versions => versions)
                    .Aggregate(
                        static (current, next) => CompareTimestamp(current.Timestamp, next.Timestamp) >= 0
                            ? current
                            : next)
                    .Timestamp;

            return Task.FromResult(
                new ExportResult(
                    changeBatches,
                    finalSnapshotTime));
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batches = new List<TimestampedEntityBatch>();
            var timestamps = request.Timestamps.Count == 0
                ? new Timestamp?[] { null }
                : request.Timestamps.ToArray();
            var entityIds = request.EntityIds.ToArray();

            for (var index = 0; index < entityIds.Length; index++)
            {
                var entityId = entityIds[index];
                var timestamp = index < timestamps.Length ? timestamps[index] : null;
                var version = this.FindVersion(entityId, timestamp);
                var entity = version is null
                    ? Array.Empty<EntitySnapshot>()
                    : new[]
                    {
                        new EntitySnapshot(
                            entityId,
                            version.ConcurrencyTag,
                            version.Timestamp,
                            version.Data?.DeepClone()),
                    };

                batches.Add(new TimestampedEntityBatch(timestamp, entity));
            }

            return Task.FromResult(new GetResult(batches));
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entities = new List<ChangedEntitySnapshot>();

            foreach (var requestedEntity in request.EntityIdTimestamps)
            {
                var currentVersion = this.FindVersion(requestedEntity.EntityId, null);
                if (currentVersion is null)
                {
                    continue;
                }

                if (CompareTimestamp(currentVersion.Timestamp, requestedEntity.Timestamp) > 0)
                {
                    entities.Add(
                        new ChangedEntitySnapshot(
                            new EntitySnapshot(
                                requestedEntity.EntityId,
                                currentVersion.ConcurrencyTag,
                                currentVersion.Timestamp,
                                currentVersion.Data?.DeepClone())));
                }
            }

            return Task.FromResult(new GetChangedEntitiesResult(entities));
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var history = new List<EntityHistoryEntry>();

            foreach (var entityId in request.EntityIds)
            {
                if (!this.entities.TryGetValue(entityId, out var versions))
                {
                    continue;
                }

                history.Add(
                    new EntityHistoryEntry(
                        entityId,
                        versions.Select(static version => version.Timestamp).ToArray()));
            }

            return Task.FromResult(new GetHistoryResult(history));
        }

        public Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batches = new List<TimestampedQueryBatch>();
            var timestamps = request.Timestamps is { Count: > 0 }
                ? request.Timestamps.ToArray()
                : new Timestamp?[] { null };

            foreach (var timestamp in timestamps)
            {
                var entitySnapshots = this.entities
                    .Select(
                        entityEntry =>
                        {
                            var version = this.FindVersion(entityEntry.Key, timestamp);
                            return version is null
                                ? null
                                : new QueryEntitySnapshot(
                                    entityEntry.Key,
                                    version.ConcurrencyTag,
                                    version.Timestamp,
                                    null,
                                    version.Data?.DeepClone(),
                                    request.Clauses.Select(clause => clause.ClauseIdentifier).ToArray(),
                                    Array.Empty<FullTextQueryScore>());
                        })
                    .Where(entitySnapshot => entitySnapshot is not null)
                    .Select(entitySnapshot => entitySnapshot!)
                    .ToArray();

                batches.Add(new TimestampedQueryBatch(timestamp, entitySnapshots));
            }

            return Task.FromResult(new QueryResult(batches));
        }

        private EntityVersion? FindVersion(
            EntityId entityId,
            Timestamp? timestamp)
        {
            if (!this.entities.TryGetValue(entityId, out var versions) || versions.Count == 0)
            {
                return null;
            }

            if (timestamp is null)
            {
                return versions[^1];
            }

            for (var index = versions.Count - 1; index >= 0; index--)
            {
                if (CompareTimestamp(versions[index].Timestamp, timestamp.Value) <= 0)
                {
                    return versions[index];
                }
            }

            return null;
        }
    }

    private static int CompareTimestamp(
        Timestamp left,
        Timestamp right)
    {
        var timeComparison = left.DateTime.CompareTo(right.DateTime);
        return timeComparison != 0
            ? timeComparison
            : StringComparer.Ordinal.Compare(left.ChangeId, right.ChangeId);
    }
}
