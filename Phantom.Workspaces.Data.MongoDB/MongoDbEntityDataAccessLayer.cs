using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbEntityDataAccessLayer : IDataAccessLayer
{
    private readonly IMongoCollection<MongoDbEntityDocument> _entityCollection;

    public MongoDbEntityDataAccessLayer(
        IMongoDatabase database,
        string collectionName)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("Collection name is required.", nameof(collectionName));
        }

        _entityCollection = database.GetCollection<MongoDbEntityDocument>($"{collectionName}_entities");
    }

    public async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<EntityUpdateResult>();
        var pendingWrites = new List<MongoDbEntityDocument>();

        var requestedEntityIds = request.Changes
            .Select(static change => ResolveEntityId(change))
            .Where(static entityId => entityId is not null)
            .Select(static entityId => entityId!.Value)
            .Distinct()
            .ToArray();

        var currentEntities = await LoadEntitiesByIdAsync(requestedEntityIds, cancellationToken).ConfigureAwait(false);

        foreach (var change in request.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entityId = ResolveEntityId(change);
            if (entityId is null)
            {
                results.Add(new EntityUpdateResult
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

            currentEntities.TryGetValue(entityId.Value.ToString(), out var currentDocument);
            var currentVersion = currentDocument?.Versions.LastOrDefault();
            var currentTag = currentVersion is null
                ? (ConcurrencyTag?)null
                : new ConcurrencyTag(currentVersion.VersionId.ToString());

            if (currentVersion is not null && IsNoContentChange(currentVersion.DataJson, change.Data))
            {
                if (change.ConcurrencyTag is not null && change.ConcurrencyTag.Value.Value != currentVersion.VersionId.ToString())
                {
                    results.Add(CreateFailedResult(entityId.Value, currentTag, currentVersion, "Concurrency tag does not match."));
                }
                else
                {
                    results.Add(new EntityUpdateResult
                    {
                        UpdateState = UpdateState.Updated,
                        RequestedEntityId = entityId.Value,
                        ResultingEntityId = entityId.Value,
                        ConcurrencyTag = currentTag,
                        ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                        CurrentEntity = CreateSnapshot(entityId.Value, currentVersion),
                        Errors = [],
                    });
                }

                continue;
            }

            if (currentVersion is not null && change.ConcurrencyTag is null)
            {
                results.Add(CreateFailedResult(entityId.Value, currentTag, currentVersion, "Concurrency tag is required."));
                continue;
            }

            if (currentVersion is not null
                && change.ConcurrencyTag is not null
                && change.ConcurrencyTag.Value.Value != currentVersion.VersionId.ToString())
            {
                results.Add(CreateFailedResult(entityId.Value, currentTag, currentVersion, "Concurrency tag does not match."));
                continue;
            }

            var nowUtc = DateTime.UtcNow;
            var nextVersionId = ObjectId.GenerateNewId(nowUtc);
            var nextTag = new ConcurrencyTag(nextVersionId.ToString());
            var nextDataJson = change.Data?.GetRawText();
            var (names, typeNames) = ExtractNamesAndTypes(change.Data);

            var updatedDocument = currentDocument ?? new MongoDbEntityDocument
            {
                Id = entityId.Value.ToString(),
                Versions = [],
            };

            updatedDocument.Versions.Add(new MongoDbEntityVersion
            {
                VersionId = nextVersionId,
                TimestampUtc = nowUtc,
                DataJson = nextDataJson,
                Names = names.ToArray(),
                TypeNames = typeNames.ToArray(),
            });

            pendingWrites.Add(updatedDocument);
            currentEntities[entityId.Value.ToString()] = updatedDocument;

            results.Add(new EntityUpdateResult
            {
                UpdateState = nextDataJson is null ? UpdateState.Removed : currentVersion is null ? UpdateState.Added : UpdateState.Updated,
                RequestedEntityId = entityId.Value,
                ResultingEntityId = entityId.Value,
                ConcurrencyTag = nextTag,
                ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                CurrentEntity = new EntitySnapshot
                {
                    EntityId = entityId.Value,
                    ConcurrencyTag = nextTag,
                    ModifiedTime = new Timestamp(new DateTimeOffset(nowUtc, TimeSpan.Zero), nextVersionId.ToString()),
                    Data = nextDataJson is null ? null : JsonDocument.Parse(nextDataJson).RootElement.Clone(),
                    Relationships = [],
                },
                Errors = [],
            });
        }

        foreach (var pendingWrite in pendingWrites)
        {
            await _entityCollection
                .ReplaceOneAsync(
                    Builders<MongoDbEntityDocument>.Filter.Eq(static document => document.Id, pendingWrite.Id),
                    pendingWrite,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new UpdateResult
        {
            EntityResults = results,
        };
    }

    public async Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timestamps = request.Timestamps?.ToArray() ?? [null];
        var allDocuments = await _entityCollection
            .Find(FilterDefinition<MongoDbEntityDocument>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var batches = new List<TimestampedEntityBatch>(timestamps.Length);
        foreach (var timestamp in timestamps)
        {
            var snapshots = new List<EntitySnapshot>();
            foreach (var getEntityRequest in request.Entities)
            {
                var matches = ResolveMatchingDocuments(allDocuments, getEntityRequest).ToArray();
                foreach (var match in matches)
                {
                    var version = ResolveVersionAtTimestamp(match, timestamp);
                    if (version is null)
                    {
                        continue;
                    }

                    var entityId = new EntityId(match.Id);
                    var snapshot = CreateSnapshot(entityId, version);
                    var relationshipRequests = getEntityRequest.RelationshipsToReturn ?? request.RelationshipsToReturn;
                    snapshot = snapshot with
                    {
                        Relationships = ResolveRelationshipsForEntity(
                            allDocuments,
                            entityId,
                            relationshipRequests),
                    };
                    snapshots.Add(snapshot);
                }
            }

            batches.Add(new TimestampedEntityBatch
            {
                Timestamp = timestamp,
                Entities = snapshots,
            });
        }

        return new GetResult
        {
            Batches = batches,
        };
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new QueryResult
        {
            Batches = request.Timestamps?.Select(timestamp => new TimestampedQueryBatch
            {
                Timestamp = timestamp,
                Entities = [],
            }).ToArray() ?? [new TimestampedQueryBatch { Timestamp = null, Entities = [] }],
        });
    }

    public async Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entities = await LoadEntitiesByIdAsync(request.EntityIds, cancellationToken).ConfigureAwait(false);
        var history = request.EntityIds
            .Where(entityId => entities.ContainsKey(entityId.ToString()))
            .Select(entityId => new EntityHistoryEntry
            {
                EntityId = entityId,
                UpdateTimes = entities[entityId.ToString()].Versions
                    .Select(static version => new Timestamp(
                        new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero),
                        version.VersionId.ToString()))
                    .Cast<Timestamp>()
                    .ToArray(),
            })
            .ToArray();

        return new GetHistoryResult
        {
            History = history,
        };
    }

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var allDocuments = await _entityCollection
            .Find(FilterDefinition<MongoDbEntityDocument>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var snapshotTime = request.SnapshotTime?.DateTime.UtcDateTime;
        var versions = allDocuments
            .SelectMany(document => document.Versions.Select(version => (Document: document, Version: version)))
            .Where(tuple => snapshotTime is null || tuple.Version.TimestampUtc >= snapshotTime.Value)
            .OrderBy(tuple => tuple.Version.TimestampUtc)
            .ThenBy(tuple => tuple.Version.VersionId)
            .ToArray();

        var batches = versions.Select(tuple => new ExportChangeBatch
        {
            ChangeTime = new Timestamp(
                new DateTimeOffset(tuple.Version.TimestampUtc, TimeSpan.Zero),
                tuple.Version.VersionId.ToString()),
            Entities =
            [
                new QueryEntitySnapshot
                {
                    EntityId = new EntityId(tuple.Document.Id),
                    ConcurrencyTag = new ConcurrencyTag(tuple.Version.VersionId.ToString()),
                    ModifiedTime = new Timestamp(
                        new DateTimeOffset(tuple.Version.TimestampUtc, TimeSpan.Zero),
                        tuple.Version.VersionId.ToString()),
                    Data = tuple.Version.DataJson is null ? null : JsonDocument.Parse(tuple.Version.DataJson).RootElement.Clone(),
                    Relationships = [],
                    MatchingClauseIdentifiers = [],
                    FullTextQueryScores = [],
                    ClassifiedTime = null,
                },
            ],
        }).ToArray();

        var finalVersion = versions.LastOrDefault().Version;
        var finalSnapshot = finalVersion is null
            ? new Timestamp(DateTimeOffset.UtcNow, ObjectId.GenerateNewId().ToString())
            : new Timestamp(new DateTimeOffset(finalVersion.TimestampUtc, TimeSpan.Zero), finalVersion.VersionId.ToString());

        return new ExportResult
        {
            ChangeBatches = batches,
            FinalSnapshotTime = finalSnapshot,
        };
    }

    public async Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entityIds = request.EntityIdTimestamps.Select(static entry => entry.EntityId).ToArray();
        var entities = await LoadEntitiesByIdAsync(entityIds, cancellationToken).ConfigureAwait(false);
        var changed = new List<ChangedEntitySnapshot>();

        foreach (var entityTimestamp in request.EntityIdTimestamps)
        {
            if (!entities.TryGetValue(entityTimestamp.EntityId.ToString(), out var document))
            {
                continue;
            }

            var hasChangeAfter = document.Versions.Any(version => IsAfter(version, entityTimestamp.Timestamp));
            if (!hasChangeAfter)
            {
                continue;
            }

            var currentVersion = document.Versions.LastOrDefault();
            changed.Add(new ChangedEntitySnapshot
            {
                Entity = currentVersion is null || currentVersion.DataJson is null
                    ? null
                    : CreateSnapshot(entityTimestamp.EntityId, currentVersion),
            });
        }

        return new GetChangedEntitiesResult
        {
            Entities = changed,
        };
    }

    private async Task<Dictionary<string, MongoDbEntityDocument>> LoadEntitiesByIdAsync(
        IReadOnlyCollection<EntityId> entityIds,
        CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
        {
            return [];
        }

        var ids = entityIds.Select(static entityId => entityId.ToString()).ToArray();
        var filter = Builders<MongoDbEntityDocument>.Filter.In(static document => document.Id, ids);
        var documents = await _entityCollection.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.ToDictionary(static document => document.Id, StringComparer.Ordinal);
    }

    private static bool IsAfter(
        MongoDbEntityVersion version,
        Timestamp timestamp)
    {
        var requestedTime = timestamp.DateTime.UtcDateTime;
        if (version.TimestampUtc > requestedTime)
        {
            return true;
        }

        return version.TimestampUtc == requestedTime
               && string.CompareOrdinal(version.VersionId.ToString(), timestamp.ChangeId) > 0;
    }

    private static MongoDbEntityVersion? ResolveVersionAtTimestamp(
        MongoDbEntityDocument document,
        Timestamp? timestamp)
    {
        if (timestamp is null)
        {
            return document.Versions.LastOrDefault();
        }

        var requestedTime = timestamp.Value.DateTime.UtcDateTime;
        return document.Versions
            .Where(version => version.TimestampUtc < requestedTime
                              || (version.TimestampUtc == requestedTime
                                  && string.CompareOrdinal(version.VersionId.ToString(), timestamp.Value.ChangeId) <= 0))
            .OrderBy(version => version.TimestampUtc)
            .ThenBy(version => version.VersionId)
            .LastOrDefault();
    }

    private static EntityId? ResolveEntityId(
        EntityChange change)
    {
        if (change.EntityId is not null)
        {
            return change.EntityId.Value;
        }

        if (change.Data is null || change.Data.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!change.Data.Value.TryGetProperty("entity-id", out var entityIdElement) || entityIdElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Guid.TryParse(entityIdElement.GetString(), out var parsedGuid) ? new EntityId(parsedGuid) : null;
    }

    private static bool IsNoContentChange(
        string? currentJson,
        JsonElement? nextData)
    {
        if (currentJson is null || nextData is null)
        {
            return currentJson is null && nextData is null;
        }

        using var currentDocument = JsonDocument.Parse(currentJson);
        return JsonElement.DeepEquals(currentDocument.RootElement, nextData.Value);
    }

    private static (IReadOnlyCollection<string> names, IReadOnlyCollection<string> typeNames) ExtractNamesAndTypes(
        JsonElement? data)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return ([], []);
        }

        var names = new List<string>();
        if (data.Value.TryGetProperty("names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in namesElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var name = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.Array)
                {
                    var components = entry.EnumerateArray()
                        .Where(static component => component.ValueKind == JsonValueKind.String)
                        .Select(static component => component.GetString())
                        .Where(static component => !string.IsNullOrWhiteSpace(component))
                        .ToArray();
                    if (components.Length > 0)
                    {
                        names.Add(string.Join('/', components!));
                    }
                }
            }
        }

        var typeNames = data.Value.ExtractStringArray("type-names").ToList();
        typeNames.AddRange(data.Value.ExtractStringArray("entity-types"));
        return (names, typeNames.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IEnumerable<MongoDbEntityDocument> ResolveMatchingDocuments(
        IReadOnlyCollection<MongoDbEntityDocument> allDocuments,
        GetEntityRequest request)
    {
        if (request.EntityId is not null)
        {
            var byId = allDocuments.FirstOrDefault(document => document.Id == request.EntityId.Value.ToString());
            return byId is null ? [] : [byId];
        }

        var requestedTypes = request.EntityTypeNames?.Values;
        if (request.EntityName is null)
        {
            return allDocuments.Where(
                document =>
                {
                    var version = document.Versions.LastOrDefault();
                    if (version is null || version.DataJson is null)
                    {
                        return false;
                    }

                    if (requestedTypes is not null && requestedTypes.Length > 0)
                    {
                        return version.TypeNames.Intersect(requestedTypes, StringComparer.Ordinal).Any();
                    }

                    return true;
                }).ToArray();
        }

        var requestedName = request.EntityName.Value.Components;

        return allDocuments.Where(document =>
        {
            var version = document.Versions.LastOrDefault();
            if (version is null || version.DataJson is null)
            {
                return false;
            }

            if (requestedTypes is not null && requestedTypes.Length > 0)
            {
                if (!version.TypeNames.Intersect(requestedTypes, StringComparer.Ordinal).Any())
                {
                    return false;
                }
            }

            foreach (var name in version.Names)
            {
                var components = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (request.EnumerateChildren == EnumerateChildrenAction.EnumerateSelf
                    && components.SequenceEqual(requestedName, StringComparer.Ordinal))
                {
                    return true;
                }

                if (request.EnumerateChildren == EnumerateChildrenAction.EnumerateChildren
                    && components.Length == requestedName.Length + 1
                    && components.Take(requestedName.Length).SequenceEqual(requestedName, StringComparer.Ordinal))
                {
                    return true;
                }

                if (request.EnumerateChildren == EnumerateChildrenAction.EnumerateAllChildren
                    && components.Length > requestedName.Length
                    && components.Take(requestedName.Length).SequenceEqual(requestedName, StringComparer.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }).ToArray();
    }

    private static IReadOnlyCollection<EntitySnapshot> ResolveRelationshipsForEntity(
        IReadOnlyCollection<MongoDbEntityDocument> allDocuments,
        EntityId entityId,
        IReadOnlyCollection<GetRelationshipRequest>? relationshipRequests)
    {
        if (relationshipRequests is null)
        {
            return [];
        }

        var relationships = new List<EntitySnapshot>();
        foreach (var document in allDocuments)
        {
            var version = document.Versions.LastOrDefault();
            if (version is null || version.DataJson is null)
            {
                continue;
            }

            using var dataDocument = JsonDocument.Parse(version.DataJson);
            var data = dataDocument.RootElement;
            if (!TryGetParticipantEntityIds(data, out var participantIds) || !participantIds.Contains(entityId))
            {
                continue;
            }

            if (!MatchesRelationshipFilter(data, relationshipRequests))
            {
                continue;
            }

            relationships.Add(new EntitySnapshot
            {
                EntityId = new EntityId(document.Id),
                ConcurrencyTag = new ConcurrencyTag(version.VersionId.ToString()),
                ModifiedTime = new Timestamp(new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero), version.VersionId.ToString()),
                Data = JsonDocument.Parse(version.DataJson).RootElement.Clone(),
                Relationships = [],
            });
        }

        return relationships;
    }

    private static bool MatchesRelationshipFilter(
        JsonElement relationshipData,
        IReadOnlyCollection<GetRelationshipRequest> relationshipRequests)
    {
        if (relationshipRequests.Count == 0)
        {
            return true;
        }

        var relationshipTypeNames = relationshipData.ExtractStringArray("entity-types");
        foreach (var request in relationshipRequests)
        {
            var typeFilter = request.RelationshipTypeNames?.Values;
            if (typeFilter is not null && typeFilter.Length > 0
                && !relationshipTypeNames.Intersect(typeFilter, StringComparer.Ordinal).Any())
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryGetParticipantEntityIds(
        JsonElement relationshipData,
        out HashSet<EntityId> participantIds)
    {
        participantIds = [];
        if (!relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object
            || !participants.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entity in entities.EnumerateArray())
        {
            if (entity.ValueKind != JsonValueKind.String
                || !Guid.TryParse(entity.GetString(), out var guid))
            {
                continue;
            }

            participantIds.Add(new EntityId(guid));
        }

        return participantIds.Count > 0;
    }

    private static EntityUpdateResult CreateFailedResult(
        EntityId entityId,
        ConcurrencyTag? currentTag,
        MongoDbEntityVersion currentVersion,
        string message)
    {
        return new EntityUpdateResult
        {
            UpdateState = UpdateState.Failed,
            RequestedEntityId = entityId,
            ResultingEntityId = entityId,
            ConcurrencyTag = currentTag,
            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
            CurrentEntity = CreateSnapshot(entityId, currentVersion),
            Errors =
            [
                new UpdateError
                {
                    Message = message,
                    RelatedEntityId = entityId,
                },
            ],
        };
    }

    private static EntitySnapshot CreateSnapshot(
        EntityId entityId,
        MongoDbEntityVersion version)
    {
        return new EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = new ConcurrencyTag(version.VersionId.ToString()),
            ModifiedTime = new Timestamp(new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero), version.VersionId.ToString()),
            Data = version.DataJson is null ? null : JsonDocument.Parse(version.DataJson).RootElement.Clone(),
            Relationships = [],
        };
    }

    private sealed class MongoDbEntityDocument
    {
        [BsonId]
        public string Id { get; init; } = string.Empty;

        public List<MongoDbEntityVersion> Versions { get; init; } = [];
    }

    private sealed class MongoDbEntityVersion
    {
        public ObjectId VersionId { get; init; }

        public DateTime TimestampUtc { get; init; }

        public string? DataJson { get; init; }

        public string[] Names { get; init; } = [];

        public string[] TypeNames { get; init; } = [];
    }
}
