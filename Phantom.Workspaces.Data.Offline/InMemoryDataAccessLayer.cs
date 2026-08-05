using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;

namespace Phantom.Workspaces.Data.Offline;

public sealed class InMemoryDataAccessLayer : IDataAccessLayer
{
    private readonly IEmbeddingsProvider embeddingsProvider;
    private readonly TimeProvider timeProvider;
    private readonly object embeddingsLock = new();
    private readonly Dictionary<EntityId, IReadOnlyList<float>> storedEmbeddings = new();
    private readonly Dictionary<string, Timestamp?> queueHeads = new(StringComparer.Ordinal);
    private State currentState = State.CreateInitial();

    public InMemoryDataAccessLayer(
        IEmbeddingsProvider? embeddingsProvider = null,
        TimeProvider? timeProvider = null)
    {
        this.embeddingsProvider = embeddingsProvider ?? new DeterministicEmbeddingsProvider();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

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
        IReadOnlyDictionary<EntityId, IReadOnlyList<float>> storedEmbeddingsSnapshot;
        lock (this.embeddingsLock)
        {
            storedEmbeddingsSnapshot = new Dictionary<EntityId, IReadOnlyList<float>>(this.storedEmbeddings);
        }

        return this.ReadState().QueryAsync(request, this.embeddingsProvider, storedEmbeddingsSnapshot, cancellationToken);
    }

    public Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var state = this.ReadState();
            var updateOutcome = state.UpdateAsync(request, this.timeProvider, cancellationToken);

            if (ReferenceEquals(
                Interlocked.CompareExchange(ref this.currentState, updateOutcome.NextState, state),
                state))
            {
                this.ClearEmbeddingsForDeletedEntities(updateOutcome.NextState);
                return Task.FromResult(updateOutcome.UpdateResult);
            }
        }
    }

    public Task<ProcessQueueResult> ProcessQueueAsync(
        ProcessQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var currentEntities = this.ReadState().GetCurrentEntities();

        lock (this.embeddingsLock)
        {
            if (request.Token is { } acknowledgedToken)
            {
                this.queueHeads[request.QueueName] = acknowledgedToken;
            }

            this.queueHeads.TryGetValue(request.QueueName, out var head);

            var batch = currentEntities
                .Where(entity => head is null || IsAfter(entity.ModifiedTime, head.Value))
                .OrderBy(entity => entity.ModifiedTime.DateTime)
                .ThenBy(entity => entity.ModifiedTime.ChangeId, StringComparer.Ordinal)
                .Take(Math.Max(0, request.Count))
                .ToArray();

            var nextToken = batch.Length > 0 ? batch[^1].ModifiedTime : head;
            return Task.FromResult(new ProcessQueueResult
            {
                Entities = batch,
                Token = nextToken,
            });
        }
    }

    public async Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(
        ComputeEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputs = request.Entities
            .Select(entity => new EmbeddingInput
            {
                EntityId = entity.EntityId,
                Text = EntityTextProjection.ProjectText(entity.Data),
            })
            .ToArray();

        var embeddings = await this.embeddingsProvider.ComputeAsync(inputs, cancellationToken).ConfigureAwait(false);

        return new ComputeEmbeddingsResult
        {
            Embeddings = embeddings
                .Select(embedding => new EntityEmbedding { EntityId = embedding.EntityId, Values = embedding.Values })
                .ToArray(),
        };
    }

    public Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(
        UpdateEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.embeddingsLock)
        {
            foreach (var update in request.Updates)
            {
                if (update.Values is null)
                {
                    this.storedEmbeddings.Remove(update.EntityId);
                }
                else
                {
                    this.storedEmbeddings[update.EntityId] = update.Values.ToArray();
                }
            }
        }

        return Task.FromResult(new UpdateEmbeddingsResult { Success = true });
    }

    private void ClearEmbeddingsForDeletedEntities(State state)
    {
        var deletedEntityIds = state.GetCurrentEntities()
            .Where(static entity => entity.Data is null)
            .Select(static entity => entity.EntityId)
            .ToArray();
        if (deletedEntityIds.Length == 0)
        {
            return;
        }

        lock (this.embeddingsLock)
        {
            foreach (var entityId in deletedEntityIds)
            {
                this.storedEmbeddings.Remove(entityId);
            }
        }
    }

    private static bool IsAfter(Timestamp candidate, Timestamp reference)
    {
        if (candidate.DateTime != reference.DateTime)
        {
            return candidate.DateTime > reference.DateTime;
        }

        return string.CompareOrdinal(candidate.ChangeId, reference.ChangeId) > 0;
    }

    private State ReadState()
    {
        return Volatile.Read(ref this.currentState);
    }

    private sealed record EntityVersion
    {
        public required Timestamp Timestamp { get; init; }

        public required ConcurrencyTag ConcurrencyTag { get; init; }

        public JsonDocument? Data { get; init; }

        public required IReadOnlyCollection<EntityName> EntityNames { get; init; }

        public required IReadOnlyCollection<string> EntityTypeNames { get; init; }
    }

    private sealed record EntityState
    {
        public required ImmutableList<EntityVersion> Versions { get; init; }

        public required ImmutableHashSet<EntityId> ParticipatingRelationshipIds { get; init; }

        public static EntityState Empty { get; } = new()
        {
            Versions = ImmutableList<EntityVersion>.Empty,
            ParticipatingRelationshipIds = ImmutableHashSet<EntityId>.Empty,
        };
    }

    private sealed record UpdateOutcome
    {
        public required State NextState { get; init; }

        public required UpdateResult UpdateResult { get; init; }
    }

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
                new StateSnapshot(ImmutableDictionary<EntityId, EntityState>.Empty),
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
            IEmbeddingsProvider embeddingsProvider,
            IReadOnlyDictionary<EntityId, IReadOnlyList<float>> storedEmbeddings,
            CancellationToken cancellationToken)
        {
            return this.snapshot.QueryAsync(request, embeddingsProvider, storedEmbeddings, cancellationToken);
        }

        public IReadOnlyList<EntitySnapshot> GetCurrentEntities()
        {
            return this.snapshot.GetCurrentEntities();
        }

        public UpdateOutcome UpdateAsync(
            UpdateRequest request,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextEntities = this.snapshot.CreateEntitiesBuilder();
            var entityBuilders = new Dictionary<EntityId, EntityBuilder>();
            var updateResults = new List<EntityUpdateResult>();
            var nextSequenceNumber = this.nextSequenceNumber;

            foreach (var change in request.Changes)
            {
                var entityId = change.EntityId ?? GetEntityId(change.Data);
                if (entityId is null)
                {
                    updateResults.Add(
                        new EntityUpdateResult
                        {
                            UpdateState = UpdateState.Failed,
                            RequestedEntityId = default,
                            ResultingEntityId = default,
                            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                            Errors =
                            [
                                new UpdateError
                                {
                                    Message = "Entity data must include an entity-id.",
                                },
                            ],
                        });
                    continue;
                }

                var entityBuilder = this.GetOrCreateEntityBuilder(entityId.Value, nextEntities, entityBuilders);
                var currentVersion = entityBuilder.CurrentVersion;

                if (currentVersion is not null
                    && this.IsNoContentChange(currentVersion, change.Data))
                {
                    if (change.ConcurrencyTag is not null
                        && currentVersion.ConcurrencyTag != change.ConcurrencyTag.Value)
                    {
                        updateResults.Add(
                            new EntityUpdateResult
                            {
                                UpdateState = UpdateState.Failed,
                                RequestedEntityId = entityId.Value,
                                ResultingEntityId = entityId.Value,
                                ConcurrencyTag = currentVersion.ConcurrencyTag,
                                ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                                CurrentEntity = this.CreateSnapshot(entityId.Value, currentVersion),
                                Errors =
                                [
                                    new UpdateError
                                    {
                                        Message = "Concurrency tag does not match.",
                                        RelatedEntityId = entityId.Value,
                                    },
                                ],
                            });
                        continue;
                    }

                    updateResults.Add(
                        new EntityUpdateResult
                        {
                            UpdateState = UpdateState.Updated,
                            RequestedEntityId = entityId.Value,
                            ResultingEntityId = entityId.Value,
                            ConcurrencyTag = currentVersion.ConcurrencyTag,
                            ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                            CurrentEntity = this.CreateSnapshot(entityId.Value, currentVersion),
                            Errors = Array.Empty<UpdateError>(),
                        });
                    continue;
                }

                if (currentVersion is not null
                    && change.ConcurrencyTag is null)
                {
                    updateResults.Add(
                        new EntityUpdateResult
                        {
                            UpdateState = UpdateState.Failed,
                            RequestedEntityId = entityId.Value,
                            ResultingEntityId = entityId.Value,
                            ConcurrencyTag = currentVersion.ConcurrencyTag,
                            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                            CurrentEntity = this.CreateSnapshot(entityId.Value, currentVersion),
                            Errors =
                            [
                                new UpdateError
                                {
                                    Message = "Concurrency tag is required.",
                                    RelatedEntityId = entityId.Value,
                                },
                            ],
                        });
                    continue;
                }

                if (change.ConcurrencyTag is not null
                    && currentVersion is not null
                    && currentVersion.ConcurrencyTag != change.ConcurrencyTag.Value)
                {
                    updateResults.Add(
                        new EntityUpdateResult
                        {
                            UpdateState = UpdateState.Failed,
                            RequestedEntityId = entityId.Value,
                            ResultingEntityId = entityId.Value,
                            ConcurrencyTag = currentVersion.ConcurrencyTag,
                            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                            CurrentEntity = this.CreateSnapshot(entityId.Value, currentVersion),
                            Errors =
                            [
                                new UpdateError
                                {
                                    Message = "Concurrency tag does not match.",
                                    RelatedEntityId = entityId.Value,
                                },
                            ],
                        });
                    continue;
                }

                nextSequenceNumber++;
                var timestamp = new Timestamp(
                    timeProvider.GetUtcNow(),
                    nextSequenceNumber.ToString());
                var entityNames = ExtractEntityNames(change.Data);
                var entityTypeNames = ExtractEntityTypeNames(change.Data);
                var data = change.Data is null ? null : JsonDocument.Parse(change.Data.Value.GetRawText());
                var concurrencyTag = new ConcurrencyTag(nextSequenceNumber.ToString());
                var previousRelatedEntityIds = ExtractRelatedEntityIds(currentVersion?.Data?.RootElement);
                var newRelatedEntityIds = ExtractRelatedEntityIds(data?.RootElement);

                entityBuilder.AddVersion(
                    new EntityVersion
                    {
                        Timestamp = timestamp,
                        ConcurrencyTag = concurrencyTag,
                        Data = data,
                        EntityNames = entityNames,
                        EntityTypeNames = entityTypeNames,
                    });

                if (newRelatedEntityIds.Count > 0 || previousRelatedEntityIds.Count > 0)
                {
                    foreach (var relatedEntityId in previousRelatedEntityIds.Concat(newRelatedEntityIds).Distinct())
                    {
                        var relatedEntityBuilder = this.GetOrCreateEntityBuilder(relatedEntityId, nextEntities, entityBuilders);
                        relatedEntityBuilder.AddParticipatingRelationship(entityId.Value);
                    }
                }

                updateResults.Add(
                    new EntityUpdateResult
                    {
                        UpdateState = change.Data is null ? UpdateState.Removed : currentVersion is null ? UpdateState.Added : UpdateState.Updated,
                        RequestedEntityId = entityId.Value,
                        ResultingEntityId = entityId.Value,
                        ConcurrencyTag = concurrencyTag,
                        ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                        CurrentEntity = new EntitySnapshot
                        {
                            EntityId = entityId.Value,
                            ConcurrencyTag = concurrencyTag,
                            ModifiedTime = timestamp,
                            Data = data?.RootElement,
                            Relationships = Array.Empty<EntitySnapshot>(),
                        },
                        Errors = Array.Empty<UpdateError>(),
                    });
            }

            foreach (var entityBuilder in entityBuilders.Values)
            {
                nextEntities[entityBuilder.EntityId] = entityBuilder.Build();
            }

            return new UpdateOutcome
            {
                NextState = new State(
                    new StateSnapshot(nextEntities.ToImmutable()),
                    nextSequenceNumber),
                UpdateResult = new UpdateResult
                {
                    EntityResults = updateResults,
                },
            };
        }

        private EntitySnapshot CreateSnapshot(
            EntityId entityId,
            EntityVersion currentVersion)
        {
            return new EntitySnapshot
            {
                EntityId = entityId,
                ConcurrencyTag = currentVersion.ConcurrencyTag,
                ModifiedTime = currentVersion.Timestamp,
                Data = currentVersion.Data?.RootElement,
                Relationships = Array.Empty<EntitySnapshot>(),
            };
        }

        private bool IsNoContentChange(
            EntityVersion currentVersion,
            JsonElement? nextData)
        {
            if (currentVersion.Data is null || nextData is null)
            {
                return currentVersion.Data is null && nextData is null;
            }

            return JsonElement.DeepEquals(currentVersion.Data.RootElement, nextData.Value);
        }

        private EntityBuilder GetOrCreateEntityBuilder(
            EntityId entityId,
            ImmutableDictionary<EntityId, EntityState>.Builder entities,
            Dictionary<EntityId, EntityBuilder> entityBuilders)
        {
            if (entityBuilders.TryGetValue(entityId, out var existingBuilder))
            {
                return existingBuilder;
            }

            entities.TryGetValue(entityId, out var existingEntity);
            var entityBuilder = new EntityBuilder(entityId, existingEntity);
            entityBuilders[entityId] = entityBuilder;
            return entityBuilder;
        }

        private static EntityId? GetEntityId(
            JsonElement? data)
        {
            if (data is null || data.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!data.Value.TryGetProperty("entity-id", out var entityIdElement)
                || entityIdElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(entityIdElement.GetString(), out var entityGuid))
            {
                return null;
            }

            return new EntityId(entityGuid);
        }

        private static IReadOnlyCollection<EntityName> ExtractEntityNames(
            JsonElement? data)
        {
            if (data is null
                || data.Value.ValueKind != JsonValueKind.Object
                || !data.Value.TryGetProperty("names", out var namesElement)
                || namesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<EntityName>();
            }

            var names = new List<EntityName>();
            foreach (var nameElement in namesElement.EnumerateArray())
            {
                var entityName = nameElement.TryReadEntityName();
                if (entityName is not null)
                {
                    names.Add(entityName.Value);
                }
            }

            return names;
        }

        private static IReadOnlyCollection<string> ExtractEntityTypeNames(
            JsonElement? data)
        {
            if (data is null
                || data.Value.ValueKind != JsonValueKind.Object
                || !data.Value.TryGetProperty("entity-types", out var entityTypesElement)
                || entityTypesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var entityTypeNames = new List<string>();
            foreach (var entityTypeElement in entityTypesElement.EnumerateArray())
            {
                if (entityTypeElement.ValueKind == JsonValueKind.String)
                {
                    var entityTypeName = entityTypeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(entityTypeName))
                    {
                        entityTypeNames.Add(entityTypeName);
                    }
                }
            }

            return entityTypeNames;
        }

        private static IReadOnlyCollection<EntityId> ExtractRelatedEntityIds(
            JsonElement? data)
        {
            if (data is null
                || data.Value.ValueKind != JsonValueKind.Object
                || !TryGetRelationshipParticipantIds(data.Value, out var relatedEntityIds))
            {
                return Array.Empty<EntityId>();
            }

            return relatedEntityIds;
        }

        private static bool TryGetRelationshipParticipantIds(
            JsonElement relationshipData,
            out IReadOnlyCollection<EntityId> participantIds)
        {
            return RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(
                relationshipData,
                out participantIds);
        }

        private sealed class EntityBuilder
        {
            private readonly EntityState state;
            private ImmutableList<EntityVersion>.Builder? versionsBuilder;
            private ImmutableHashSet<EntityId>.Builder? participatingRelationshipIdsBuilder;

            public EntityBuilder(
                EntityId entityId,
                EntityState? existingEntity)
            {
                this.EntityId = entityId;
                this.state = existingEntity ?? EntityState.Empty;
            }

            public EntityId EntityId { get; }

            public EntityVersion? CurrentVersion
            {
                get
                {
                    IReadOnlyList<EntityVersion> versions;
                    if (this.versionsBuilder is not null)
                    {
                        versions = this.versionsBuilder;
                    }
                    else
                    {
                        versions = this.state.Versions;
                    }

                    return versions.Count == 0 ? null : versions[^1];
                }
            }

            public void AddVersion(
                EntityVersion version)
            {
                this.versionsBuilder ??= this.state.Versions.ToBuilder();
                this.versionsBuilder.Add(version);
            }

            public void AddParticipatingRelationship(
                EntityId relationshipEntityId)
            {
                this.participatingRelationshipIdsBuilder ??= this.state.ParticipatingRelationshipIds.ToBuilder();
                this.participatingRelationshipIdsBuilder.Add(relationshipEntityId);
            }

            public EntityState Build()
            {
                return new EntityState
                {
                    Versions = this.versionsBuilder?.ToImmutable() ?? this.state.Versions,
                    ParticipatingRelationshipIds = this.participatingRelationshipIdsBuilder?.ToImmutable() ?? this.state.ParticipatingRelationshipIds,
                };
            }
        }
    }

    private sealed class StateSnapshot
    {
        private readonly ImmutableDictionary<EntityId, EntityState> entities;

        public StateSnapshot(
            ImmutableDictionary<EntityId, EntityState> entities)
        {
            this.entities = entities;
        }

        public ImmutableDictionary<EntityId, EntityState>.Builder CreateEntitiesBuilder()
        {
            return this.entities.ToBuilder();
        }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changeBatches = new List<ExportChangeBatch>();

            foreach (var entityEntry in this.entities)
            {
                foreach (var version in entityEntry.Value.Versions)
                {
                    if (request.SnapshotTime is not null
                        && CompareTimestamp(version.Timestamp, request.SnapshotTime.Value) <= 0)
                    {
                        continue;
                    }

                    changeBatches.Add(
                        new ExportChangeBatch
                        {
                            ChangeTime = version.Timestamp,
                            Entities =
                            [
                                new QueryEntitySnapshot
                                {
                                    EntityId = entityEntry.Key,
                                    ConcurrencyTag = version.ConcurrencyTag,
                                    ModifiedTime = version.Timestamp,
                                    Data = version.Data?.RootElement,
                                    Relationships = Array.Empty<EntitySnapshot>(),
                                    MatchingClauseIdentifiers = Array.Empty<QueryClauseIdentifier>(),
                                },
                            ],
                        });
                }
            }

            changeBatches.Sort(
                static (left, right) => CompareTimestamp(left.ChangeTime, right.ChangeTime));

            var latestVersion = this.entities.Values
                .SelectMany(static entity => entity.Versions)
                .OrderByDescending(static version => version.Timestamp.DateTime)
                .ThenByDescending(static version => version.Timestamp.ChangeId, StringComparer.Ordinal)
                .FirstOrDefault();
            var finalSnapshotTime = latestVersion?.Timestamp ?? new Timestamp(DateTimeOffset.UnixEpoch, "0");

            return Task.FromResult(
                new ExportResult
                {
                    ChangeBatches = changeBatches,
                    FinalSnapshotTime = finalSnapshotTime,
                });
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timestamps = request.Timestamps is { Count: > 0 }
                ? request.Timestamps.ToArray()
                : new Timestamp?[] { null };
            var batches = new List<TimestampedEntityBatch>();
            var requestedEntities = request.Entities;

            foreach (var timestamp in timestamps)
            {
                var entities = new List<EntitySnapshot>();
                var includedEntityIds = new HashSet<EntityId>();
                foreach (var requestedEntity in requestedEntities)
                {
                    foreach (var entityId in this.FindEntityIds(requestedEntity, timestamp))
                    {
                        if (!includedEntityIds.Add(entityId))
                        {
                            continue;
                        }

                        var version = this.FindVersion(entityId, timestamp);
                        if (version is null)
                        {
                            continue;
                        }

                        var relationshipFilter = requestedEntity.RelationshipsToReturn ?? request.RelationshipsToReturn;
                        var relationships = this.GetRelationshipsForEntity(entityId, timestamp, relationshipFilter);

                        entities.Add(
                            new EntitySnapshot
                            {
                                EntityId = entityId,
                                ConcurrencyTag = version.ConcurrencyTag,
                                ModifiedTime = version.Timestamp,
                                Data = version.Data?.RootElement,
                                Relationships = relationships,
                            });
                    }
                }

                batches.Add(
                    new TimestampedEntityBatch
                    {
                        Timestamp = timestamp,
                        Entities = entities,
                    });
            }

            return Task.FromResult(
                new GetResult
                {
                    Batches = batches,
                });
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
                        new ChangedEntitySnapshot
                        {
                            Entity = new EntitySnapshot
                            {
                                EntityId = requestedEntity.EntityId,
                                ConcurrencyTag = currentVersion.ConcurrencyTag,
                                ModifiedTime = currentVersion.Timestamp,
                                Data = currentVersion.Data?.RootElement,
                                Relationships = Array.Empty<EntitySnapshot>(),
                            },
                        });
                }
            }

            return Task.FromResult(
                new GetChangedEntitiesResult
                {
                    Entities = entities,
                });
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var history = new List<EntityHistoryEntry>();

            foreach (var entityId in request.EntityIds)
            {
                if (!this.entities.TryGetValue(entityId, out var entityState)
                    || entityState.Versions.Count == 0)
                {
                    continue;
                }

                history.Add(
                    new EntityHistoryEntry
                    {
                        EntityId = entityId,
                        UpdateTimes = entityState.Versions.Select(static version => version.Timestamp).ToArray(),
                    });
            }

            return Task.FromResult(
                new GetHistoryResult
                {
                    History = history,
                });
        }

        public async Task<QueryResult> QueryAsync(
            QueryRequest request,
            IEmbeddingsProvider embeddingsProvider,
            IReadOnlyDictionary<EntityId, IReadOnlyList<float>> storedEmbeddings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batches = new List<TimestampedQueryBatch>();
            var timestamps = request.Timestamps is { Count: > 0 }
                ? request.Timestamps.ToArray()
                : new Timestamp?[] { null };

            foreach (var timestamp in timestamps)
            {
                var versionsById = new Dictionary<EntityId, EntityVersion>();
                var candidates = new List<InMemoryQueryEvaluator.Candidate>();
                foreach (var entityEntry in this.entities)
                {
                    var version = this.FindVersion(entityEntry.Key, timestamp);
                    if (version is null)
                    {
                        continue;
                    }

                    versionsById[entityEntry.Key] = version;
                    storedEmbeddings.TryGetValue(entityEntry.Key, out var storedEmbedding);
                    candidates.Add(new InMemoryQueryEvaluator.Candidate(
                        entityEntry.Key,
                        version.Data?.RootElement,
                        storedEmbedding));
                }

                var evaluator = new InMemoryQueryEvaluator(candidates, embeddingsProvider);
                var evaluated = await evaluator.EvaluateAsync(request.Clauses, cancellationToken).ConfigureAwait(false);

                var entitySnapshots = evaluated
                    .Select(entity =>
                    {
                        var version = versionsById[entity.Id];
                        return new QueryEntitySnapshot
                        {
                            EntityId = entity.Id,
                            ConcurrencyTag = version.ConcurrencyTag,
                            ModifiedTime = version.Timestamp,
                            Data = version.Data?.RootElement,
                            Relationships = this.GetRelationshipsForEntity(entity.Id, timestamp, request.RelationshipsToReturn),
                            MatchingClauseIdentifiers = entity.MatchingClauseIdentifiers,
                            VectorQueryScores = entity.VectorScores,
                        };
                    })
                    .ToArray();

                batches.Add(
                    new TimestampedQueryBatch
                    {
                        Timestamp = timestamp,
                        Entities = entitySnapshots,
                    });
            }

            return new QueryResult
            {
                Batches = batches,
            };
        }

        public IReadOnlyList<EntitySnapshot> GetCurrentEntities()
        {
            var result = new List<EntitySnapshot>();
            foreach (var entityEntry in this.entities)
            {
                var versions = entityEntry.Value.Versions;
                if (versions.Count == 0)
                {
                    continue;
                }

                var latest = versions[^1];
                result.Add(new EntitySnapshot
                {
                    EntityId = entityEntry.Key,
                    ConcurrencyTag = latest.ConcurrencyTag,
                    ModifiedTime = latest.Timestamp,
                    Data = latest.Data?.RootElement,
                    Relationships = Array.Empty<EntitySnapshot>(),
                });
            }

            return result;
        }

        private EntityVersion? FindVersion(
            EntityId entityId,
            Timestamp? timestamp)
        {
            if (!this.entities.TryGetValue(entityId, out var entityState) || entityState.Versions.Count == 0)
            {
                return null;
            }

            var versions = entityState.Versions;
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

        private IEnumerable<EntityId> FindEntityIds(
            GetEntityRequest request,
            Timestamp? timestamp)
        {
            if (request.EntityId is not null)
            {
                var entityVersion = this.FindVersion(request.EntityId.Value, timestamp);
                if (entityVersion is not null && this.MatchesEntityCriteria(entityVersion, request))
                {
                    yield return request.EntityId.Value;
                }

                yield break;
            }

            foreach (var entityEntry in this.entities)
            {
                var version = this.FindVersion(entityEntry.Key, timestamp);
                if (version is null)
                {
                    continue;
                }

                if (this.MatchesEntityCriteria(version, request))
                {
                    yield return entityEntry.Key;
                }
            }
        }

        private bool MatchesEntityCriteria(
            EntityVersion version,
            GetEntityRequest request)
        {
            if (request.EntityName is not null
                && !version.EntityNames.Any(entityName => MatchesEntityName(entityName, request.EntityName.Value, request.EnumerateChildren)))
            {
                return false;
            }

            if (request.EntityTypeNames is not null
                && !request.EntityTypeNames.Value.Values.All(typeName => version.EntityTypeNames.Contains(typeName, StringComparer.Ordinal)))
            {
                return false;
            }

            return true;
        }

        private static bool MatchesEntityName(
            EntityName candidateName,
            EntityName requestedName,
            EnumerateChildrenAction enumerateChildren)
        {
            var candidateComponents = candidateName.Components;
            var requestedComponents = requestedName.Components;
            if (!candidateComponents.Take(requestedComponents.Length).SequenceEqual(requestedComponents, StringComparer.Ordinal))
            {
                return false;
            }

            return enumerateChildren switch
            {
                EnumerateChildrenAction.EnumerateSelf => candidateComponents.Length == requestedComponents.Length,
                EnumerateChildrenAction.EnumerateChildren => candidateComponents.Length == requestedComponents.Length + 1,
                EnumerateChildrenAction.EnumerateAllChildren => candidateComponents.Length > requestedComponents.Length,
                _ => false,
            };
        }

        private IReadOnlyCollection<EntitySnapshot> GetRelationshipsForEntity(
            EntityId entityId,
            Timestamp? timestamp,
            IReadOnlyCollection<GetRelationshipRequest>? relationshipFilters)
        {
            if (relationshipFilters is null
                || !this.entities.TryGetValue(entityId, out var entityState)
                || entityState.ParticipatingRelationshipIds.Count == 0)
            {
                return Array.Empty<EntitySnapshot>();
            }

            var relationships = new List<EntitySnapshot>();
            foreach (var relationshipId in entityState.ParticipatingRelationshipIds)
            {
                var relationshipVersion = this.FindVersion(relationshipId, timestamp);
                if (relationshipVersion is null
                    || relationshipVersion.Data is null
                    || !this.IsRelationshipForEntity(relationshipVersion.Data.RootElement, entityId))
                {
                    continue;
                }

                if (relationshipFilters.Count > 0
                    && !relationshipFilters.Any(filter => this.MatchesRelationshipFilter(relationshipVersion, filter)))
                {
                    continue;
                }

                relationships.Add(
                    new EntitySnapshot
                    {
                        EntityId = relationshipId,
                        ConcurrencyTag = relationshipVersion.ConcurrencyTag,
                        ModifiedTime = relationshipVersion.Timestamp,
                        Data = relationshipVersion.Data.RootElement,
                        Relationships = Array.Empty<EntitySnapshot>(),
                    });
            }

            return relationships;
        }

        private bool IsRelationshipForEntity(
            JsonElement relationshipData,
            EntityId entityId)
        {
            if (relationshipData.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var entityIdText = entityId.ToString();
            if (relationshipData.TryGetProperty("participants", out var participants)
                && participants.ValueKind == JsonValueKind.Object
                && this.ContainsParticipantId(participants, entityIdText))
            {
                return true;
            }

            return false;
        }

        private bool ContainsParticipantId(
            JsonElement value,
            string entityIdText)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (this.ContainsParticipantId(property.Value, entityIdText))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (this.ContainsParticipantId(item, entityIdText))
                    {
                        return true;
                    }
                }

                return false;
            }

            return value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), entityIdText, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesRelationshipFilter(
            EntityVersion relationshipVersion,
            GetRelationshipRequest filter)
        {
            if (filter.RelationshipTypeNames is not null
                && !this.MatchesRelationshipTypeNames(relationshipVersion, filter.RelationshipTypeNames.Value.Values))
            {
                return false;
            }

            if (filter.RelationshipRoleNames is null)
            {
                return true;
            }

            if (relationshipVersion.Data is null
                || !relationshipVersion.Data.RootElement.TryGetProperty("participants", out var participantsElement)
                || participantsElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var availableRoles = participantsElement.EnumerateObject()
                .Select(static role => role.Name)
                .ToHashSet(StringComparer.Ordinal);
            return filter.RelationshipRoleNames.Value.Values.All(availableRoles.Contains);
        }

        private bool MatchesRelationshipTypeNames(
            EntityVersion relationshipVersion,
            IReadOnlyCollection<string> requestedTypeNames)
        {
            var explicitTypeNames = relationshipVersion.EntityTypeNames.ToHashSet(StringComparer.Ordinal);
            foreach (var requestedTypeName in requestedTypeNames)
            {
                if (explicitTypeNames.Contains(requestedTypeName))
                {
                    continue;
                }

                if (string.Equals(requestedTypeName, "relationship", StringComparison.Ordinal)
                    && this.IsRelationshipShape(relationshipVersion.Data))
                {
                    continue;
                }

                if (string.Equals(requestedTypeName, "reference", StringComparison.Ordinal)
                    && this.IsReferenceRelationshipShape(relationshipVersion.Data))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private bool IsRelationshipShape(
            JsonDocument? data)
        {
            if (data is null || data.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (data.RootElement.TryGetProperty("entities", out var entities)
                && entities.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            if (data.RootElement.TryGetProperty("participants", out var participants)
                && participants.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            return this.IsReferenceRelationshipShape(data);
        }

        private bool IsReferenceRelationshipShape(
            JsonDocument? data)
        {
            if (data is null || data.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (data.RootElement.TryGetProperty("entity-types", out var entityTypes)
                && entityTypes.ValueKind == JsonValueKind.Array
                && entityTypes.EnumerateArray().Any(type => type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "reference", StringComparison.Ordinal)))
            {
                return true;
            }

            return data.RootElement.TryGetProperty("source", out var sourceEntityId)
                && data.RootElement.TryGetProperty("target", out var targetEntityId)
                && sourceEntityId.ValueKind == JsonValueKind.String
                && targetEntityId.ValueKind == JsonValueKind.String;
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
