using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbEntityDataAccessLayer : IDataAccessLayer
{
    /// <summary>The Atlas vector search index name over the current-version embedding field.</summary>
    public const string VectorIndexName = "entity-current-embedding-index";

    private const int VectorIndexRemovalPollAttempts = 30;
    private static readonly TimeSpan VectorIndexRemovalPollInterval = TimeSpan.FromSeconds(2);

    private readonly IMongoCollection<MongoDbEntityDocument> _entityCollection;
    private readonly Phantom.Workspaces.Data.Vector.IEmbeddingsProvider _embeddingsProvider;

    public MongoDbEntityDataAccessLayer(
        IMongoDatabase database,
        string collectionName,
        Phantom.Workspaces.Data.Vector.IEmbeddingsProvider? embeddingsProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("Collection name is required.", nameof(collectionName));
        }

        _entityCollection = database.GetCollection<MongoDbEntityDocument>($"{collectionName}_entities");
        _embeddingsProvider = embeddingsProvider ?? new Phantom.Workspaces.Data.Vector.DeterministicEmbeddingsProvider();
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

            // Recompute the denormalized current-version projection used for native querying.
            var projectedText = Phantom.Workspaces.Data.Vector.EntityTextProjection.ProjectText(change.Data);
            float[]? embedding = null;
            if (nextDataJson is not null && !string.IsNullOrWhiteSpace(projectedText))
            {
                var embeddings = await _embeddingsProvider.ComputeAsync(
                    [new Phantom.Workspaces.Data.Vector.EmbeddingInput { EntityId = entityId.Value, Text = projectedText }],
                    cancellationToken).ConfigureAwait(false);
                embedding = embeddings[0].Values.ToArray();
            }

            updatedDocument.Current = new MongoDbCurrentProjection
            {
                TypeNames = typeNames.ToArray(),
                Embedding = embedding,
                IsDeleted = nextDataJson is null,
            };

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

    public async Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timestamps = request.Timestamps is { Count: > 0 }
            ? request.Timestamps.ToArray()
            : new Timestamp?[] { null };

        // Native querying targets the denormalized current-version projection, so it only supports
        // "now" (null timestamp) queries. As-of-timestamp querying is a follow-up.
        if (timestamps.Any(static timestamp => timestamp is not null))
        {
            throw new NotSupportedException(
                "MongoDB query evaluation currently supports only current (null-timestamp) queries.");
        }

        var bsonCollection = _entityCollection.Database.GetCollection<BsonDocument>(
            _entityCollection.CollectionNamespace.CollectionName);
        var translator = new MongoDbQueryTranslator();

        var batches = new List<TimestampedQueryBatch>();
        foreach (var timestamp in timestamps)
        {
            var matchedClauses = new Dictionary<string, HashSet<QueryClauseIdentifier>>(StringComparer.Ordinal);
            var documentsById = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
            var vectorScoredEntities = new Dictionary<string, QueryEntitySnapshot>(StringComparer.Ordinal);

            foreach (var topLevelClause in request.Clauses)
            {
                if (topLevelClause.Clause is EntityVectorQueryClause vectorClause)
                {
                    var vectorMatches = await ExecuteVectorClauseAsync(
                        bsonCollection, topLevelClause, vectorClause, cancellationToken).ConfigureAwait(false);
                    foreach (var vectorMatch in vectorMatches)
                    {
                        vectorScoredEntities[vectorMatch.EntityId.ToString()] = vectorMatch;
                        if (!matchedClauses.TryGetValue(vectorMatch.EntityId.ToString(), out var vectorIdentifiers))
                        {
                            matchedClauses[vectorMatch.EntityId.ToString()] = vectorIdentifiers = [];
                        }

                        vectorIdentifiers.Add(topLevelClause.ClauseIdentifier);
                    }

                    continue;
                }

                var filter = translator.TranslateToFilter(topLevelClause.Clause);
                var find = bsonCollection.Find(filter);
                if (MongoDbQueryTranslator.GetResultLimit(topLevelClause.Clause) is { } limit && limit >= 0)
                {
                    find = find.Limit(limit);
                }

                var documents = await find.ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var document in documents)
                {
                    var id = document["_id"].AsString;
                    documentsById[id] = document;
                    if (!matchedClauses.TryGetValue(id, out var identifiers))
                    {
                        matchedClauses[id] = identifiers = [];
                    }

                    identifiers.Add(topLevelClause.ClauseIdentifier);
                }
            }

            var entities = new List<QueryEntitySnapshot>();
            foreach (var (id, identifiers) in matchedClauses)
            {
                if (vectorScoredEntities.TryGetValue(id, out var vectorEntity))
                {
                    entities.Add(vectorEntity with { MatchingClauseIdentifiers = identifiers.ToArray() });
                    continue;
                }

                var snapshot = BuildCurrentSnapshot(documentsById[id]);
                if (snapshot is not null)
                {
                    entities.Add(snapshot with { MatchingClauseIdentifiers = identifiers.ToArray() });
                }
            }

            batches.Add(new TimestampedQueryBatch { Timestamp = timestamp, Entities = entities });
        }

        return new QueryResult { Batches = batches };
    }

    /// <summary>
    /// Ensures the Atlas vector search index over the current-version embedding field exists and is
    /// in a functional state. This requires an Atlas-capable deployment (Atlas, or the
    /// mongodb/mongodb-atlas-local image); community MongoDB does not support search indexes.
    /// </summary>
    /// <remarks>
    /// If an index with the expected name exists but is in a terminal non-functional state (for
    /// example, it was orphaned by dropping and recreating the underlying collection, leaving it
    /// reported as <c>DOES_NOT_EXIST</c> or <c>FAILED</c>), it is dropped and recreated so the index
    /// self-heals. Indexes that are still building (<c>PENDING</c>/<c>BUILDING</c>) or ready are left
    /// as-is.
    /// </remarks>
    public async Task EnsureVectorIndexAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _entityCollection.SearchIndexes
            .List()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var current = existing.FirstOrDefault(
            index => index.GetValue("name", BsonString.Empty).AsString == VectorIndexName);
        if (current is not null)
        {
            if (IsFunctionalVectorIndex(current))
            {
                return;
            }

            // The index name is registered but the index is dead (for example, orphaned by a
            // collection drop). Drop it and wait for the deletion to settle so the recreate below
            // does not race an in-progress delete of the same index name.
            await _entityCollection.SearchIndexes
                .DropOneAsync(VectorIndexName, cancellationToken)
                .ConfigureAwait(false);
            await WaitForVectorIndexRemovalAsync(cancellationToken).ConfigureAwait(false);
        }

        var definition = new BsonDocument
        {
            {
                "fields",
                new BsonArray
                {
                    new BsonDocument
                    {
                        { "type", "vector" },
                        { "path", MongoDbQueryTranslator.EmbeddingField },
                        { "numDimensions", _embeddingsProvider.Dimensions },
                        { "similarity", "cosine" },
                    },
                }
            },
        };

        await _entityCollection.SearchIndexes
            .CreateOneAsync(
                new CreateSearchIndexModel(VectorIndexName, SearchIndexType.VectorSearch, definition),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether an Atlas search index document describes an index that exists and is
    /// either ready or actively building. A terminal status such as <c>DOES_NOT_EXIST</c> or
    /// <c>FAILED</c> indicates a dead index that must be recreated.
    /// </summary>
    private static bool IsFunctionalVectorIndex(BsonDocument index)
    {
        var status = index.GetValue("status", BsonString.Empty).AsString;
        return status switch
        {
            "DOES_NOT_EXIST" or "FAILED" => false,
            _ => true,
        };
    }

    /// <summary>
    /// Polls until the vector search index name is no longer reported by the deployment, so a
    /// freshly issued drop has fully settled before the index is recreated under the same name.
    /// </summary>
    private async Task WaitForVectorIndexRemovalAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < VectorIndexRemovalPollAttempts; attempt++)
        {
            var indexes = await _entityCollection.SearchIndexes
                .List()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!indexes.Any(index => index.GetValue("name", BsonString.Empty).AsString == VectorIndexName))
            {
                return;
            }

            await Task.Delay(VectorIndexRemovalPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<QueryEntitySnapshot>> ExecuteVectorClauseAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        TopLevelQueryClause topLevelClause,
        EntityVectorQueryClause vectorClause,
        CancellationToken cancellationToken)
    {
        var queryEmbedding = vectorClause.QueryEmbedding;
        if (queryEmbedding is not { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(vectorClause.QueryText))
            {
                throw new ArgumentException("A vector query clause requires query-text or a query-embedding.");
            }

            var computed = await _embeddingsProvider.ComputeAsync(
                [new Phantom.Workspaces.Data.Vector.EmbeddingInput { EntityId = default, Text = vectorClause.QueryText! }],
                cancellationToken).ConfigureAwait(false);
            queryEmbedding = computed[0].Values;
            vectorClause = vectorClause with { QueryEmbedding = queryEmbedding };
        }

        var vectorStage = MongoDbQueryTranslator.BuildVectorSearchStage(vectorClause, VectorIndexName);
        var pipeline = new[]
        {
            vectorStage,
            new BsonDocument("$addFields", new BsonDocument("vector-score", new BsonDocument("$meta", "vectorSearchScore"))),
        };

        var documents = await bsonCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var snapshots = new List<QueryEntitySnapshot>();
        foreach (var document in documents)
        {
            var snapshot = BuildCurrentSnapshot(document);
            if (snapshot is null)
            {
                continue;
            }

            var score = document.TryGetValue("vector-score", out var scoreValue) ? scoreValue.ToDouble() : 0d;
            snapshots.Add(snapshot with
            {
                MatchingClauseIdentifiers = [topLevelClause.ClauseIdentifier],
                VectorQueryScores =
                [
                    new VectorQueryScore { QueryIdentifier = vectorClause.VectorQueryIdentifier, Score = score },
                ],
            });
        }

        return snapshots;
    }

    private static QueryEntitySnapshot? BuildCurrentSnapshot(BsonDocument document)
    {
        if (!document.TryGetValue("Versions", out var versionsValue) || versionsValue is not BsonArray { Count: > 0 } versions)
        {
            return null;
        }

        var latest = versions[^1].AsBsonDocument;
        var dataJson = latest.TryGetValue("DataJson", out var dataJsonValue) && !dataJsonValue.IsBsonNull
            ? dataJsonValue.AsString
            : null;
        if (dataJson is null)
        {
            return null;
        }

        var versionId = latest["VersionId"].AsObjectId;
        var timestampUtc = latest["TimestampUtc"].ToUniversalTime();
        var modifiedTime = new Timestamp(new DateTimeOffset(timestampUtc, TimeSpan.Zero), versionId.ToString());

        return new QueryEntitySnapshot
        {
            EntityId = new EntityId(document["_id"].AsString),
            ConcurrencyTag = new ConcurrencyTag(versionId.ToString()),
            ModifiedTime = modifiedTime,
            Data = JsonDocument.Parse(dataJson).RootElement.Clone(),
            Relationships = [],
            MatchingClauseIdentifiers = [],
        };
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

        /// <summary>
        /// Denormalized projection of the latest version, used for native query-clause evaluation
        /// (see <see cref="MongoDbQueryTranslator"/>). Recomputed on every write.
        /// </summary>
        [BsonElement("current")]
        [BsonIgnoreIfNull]
        public MongoDbCurrentProjection? Current { get; set; }
    }

    private sealed class MongoDbCurrentProjection
    {
        [BsonElement("type-names")]
        public string[] TypeNames { get; init; } = [];

        [BsonElement("embedding")]
        [BsonIgnoreIfNull]
        public float[]? Embedding { get; init; }

        [BsonElement("is-deleted")]
        public bool IsDeleted { get; init; }
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
