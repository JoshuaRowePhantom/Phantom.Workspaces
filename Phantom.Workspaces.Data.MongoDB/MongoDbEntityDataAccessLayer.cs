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
    private readonly IMongoCollection<MongoDbQueueHead> _queueHeadCollection;
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
        _queueHeadCollection = database.GetCollection<MongoDbQueueHead>($"{collectionName}_queue_heads");
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

            if (currentVersion is not null && IsNoContentChange(currentVersion.Data, change.Data))
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
            var hasData = change.Data is not null;
            var nextDataBson = hasData ? MongoEntityData.ToBsonDocument(change.Data!.Value) : null;

            var updatedDocument = currentDocument ?? new MongoDbEntityDocument
            {
                Id = entityId.Value.ToString(),
                Versions = [],
            };

            updatedDocument.Versions.Add(new MongoDbEntityVersion
            {
                VersionId = nextVersionId,
                TimestampUtc = nowUtc,
                Data = nextDataBson,
            });

            // Recompute the denormalized current-version projection used for native querying.
            var projectedText = Phantom.Workspaces.Data.Vector.EntityTextProjection.ProjectText(change.Data);
            float[]? embedding = null;
            if (hasData && !string.IsNullOrWhiteSpace(projectedText))
            {
                var embeddings = await _embeddingsProvider.ComputeAsync(
                    [new Phantom.Workspaces.Data.Vector.EmbeddingInput { EntityId = entityId.Value, Text = projectedText }],
                    cancellationToken).ConfigureAwait(false);
                embedding = embeddings[0].Values.ToArray();
            }

            var participantIds = ExtractParticipantIds(change.Data);
            var nameParentPrefixes = hasData && nextDataBson is not null
                ? ComputeNameParentPrefixes(nextDataBson)
                : [];

            updatedDocument.Current = new MongoDbCurrentProjection
            {
                Data = nextDataBson,
                ParticipantIds = participantIds,
                NameParentPrefixes = nameParentPrefixes,
                Embedding = embedding,
                IsDeleted = !hasData,
                ModifiedTimeUtc = nowUtc,
                ModifiedVersion = nextVersionId.ToString(),
            };

            pendingWrites.Add(updatedDocument);
            currentEntities[entityId.Value.ToString()] = updatedDocument;

            results.Add(new EntityUpdateResult
            {
                UpdateState = !hasData ? UpdateState.Removed : currentVersion is null ? UpdateState.Added : UpdateState.Updated,
                RequestedEntityId = entityId.Value,
                ResultingEntityId = entityId.Value,
                ConcurrencyTag = nextTag,
                ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                CurrentEntity = new EntitySnapshot
                {
                    EntityId = entityId.Value,
                    ConcurrencyTag = nextTag,
                    ModifiedTime = new Timestamp(new DateTimeOffset(nowUtc, TimeSpan.Zero), nextVersionId.ToString()),
                    Data = change.Data?.Clone(),
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

        if (request.Entities.Count == 0)
        {
            return new GetResult
            {
                Batches = timestamps
                    .Select(static t => new TimestampedEntityBatch { Timestamp = t, Entities = [] })
                    .ToList(),
            };
        }

        // Build a targeted MongoDB filter from the entity sub-requests.
        var entityFilterDocument = BuildGetFilterDocument(request.Entities);
        var entityDocuments = await _entityCollection
            .Find(new BsonDocumentFilterDefinition<MongoDbEntityDocument>(entityFilterDocument))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // If relationships are requested, also load relationship documents not already in the
        // entity result set so that ResolveRelationshipsForEntity can find them.
        List<MongoDbEntityDocument> allDocuments = entityDocuments;
        var hasRelationshipRequests = request.RelationshipsToReturn != null
                                      || request.Entities.Any(static e => e.RelationshipsToReturn != null);
        if (hasRelationshipRequests && entityDocuments.Count > 0)
        {
            var loadedIds = entityDocuments.Select(static d => d.Id).ToHashSet(StringComparer.Ordinal);
            var relationshipDocFilter = new BsonDocumentFilterDefinition<MongoDbEntityDocument>(
                new BsonDocument
                {
                    { MongoDbQueryTranslator.IsDeletedField, new BsonDocument("$ne", true) },
                    { "current.data.participants", new BsonDocument("$exists", true) },
                });
            var relationshipDocs = await _entityCollection
                .Find(relationshipDocFilter)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var extra = relationshipDocs.Where(d => !loadedIds.Contains(d.Id)).ToList();
            if (extra.Count > 0)
            {
                allDocuments = [.. entityDocuments, .. extra];
            }
        }

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

    /// <summary>
    /// Builds a targeted MongoDB filter document from a list of entity sub-requests.
    /// Returns an empty document (<c>{}</c>) when a full collection scan is required.
    /// </summary>
    /// <remarks>
    /// The returned filter should be used as a pre-filter only; <c>ResolveMatchingDocuments</c>
    /// applies the authoritative in-memory post-filter for correctness.
    /// </remarks>
    internal static BsonDocument BuildGetFilterDocument(IReadOnlyCollection<GetEntityRequest> entities)
    {
        if (entities.Count == 0)
        {
            return new BsonDocument();
        }

        var perRequestClauses = new BsonArray();

        foreach (var entity in entities)
        {
            if (entity.EntityId is { } entityId)
            {
                perRequestClauses.Add(new BsonDocument("_id", entityId.ToString()));
                continue;
            }

            var subClauses = new List<BsonDocument>();

            if (entity.EntityTypeNames?.Values is { Length: > 0 } typeNames)
            {
                subClauses.Add(new BsonDocument(MongoDbGetFilterBuilder.EntityTypesField,
                    new BsonDocument("$in", new BsonArray(typeNames.Select(n => (BsonValue)new BsonString(n))))));
            }

            if (entity.EntityName is { } entityName)
            {
                switch (entity.EnumerateChildren)
                {
                    case EnumerateChildrenAction.EnumerateSelf:
                        subClauses.Add(new BsonDocument(MongoDbGetFilterBuilder.NamesField,
                            new BsonArray(entityName.Components.Select(c => (BsonValue)new BsonString(c)))));
                        break;

                    case EnumerateChildrenAction.EnumerateChildren:
                        var prefixClauseChildren = new BsonDocument(MongoDbGetFilterBuilder.NameParentPrefixesField,
                            new BsonArray(entityName.Components.Select(c => (BsonValue)new BsonString(c))));
                        subClauses.Add(prefixClauseChildren);
                        break;

                    case EnumerateChildrenAction.EnumerateAllChildren:
                        var prefixClauseAll = new BsonDocument(MongoDbGetFilterBuilder.NameParentPrefixesField,
                            new BsonArray(entityName.Components.Select(c => (BsonValue)new BsonString(c))));
                        subClauses.Add(prefixClauseAll);
                        break;
                }
            }

            if (subClauses.Count > 0)
            {
                var clause = subClauses.Count == 1
                    ? subClauses[0]
                    : new BsonDocument("$and", new BsonArray(subClauses));
                perRequestClauses.Add(clause);
            }
            else
            {
                return new BsonDocument();
            }
        }

        return perRequestClauses.Count == 1
            ? perRequestClauses[0].AsBsonDocument
            : new BsonDocument("$or", perRequestClauses);
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
        var translator = new MongoDbQueryFilterBuilder();

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

                // Non-vector clauses resolve to their matching entity documents. Participation clauses
                // (optionally composed with Not(participation) exclusions) are evaluated natively as a
                // single aggregation join; field/type filters use a find.
                var matchedDocuments = await this.ExecuteEntityClauseAsync(
                    bsonCollection, translator, topLevelClause.Clause, cancellationToken).ConfigureAwait(false);
                foreach (var matchedDocument in matchedDocuments)
                {
                    var matchedId = matchedDocument["_id"].AsString;
                    documentsById[matchedId] = matchedDocument;
                    if (!matchedClauses.TryGetValue(matchedId, out var identifiers))
                    {
                        matchedClauses[matchedId] = identifiers = [];
                    }

                    identifiers.Add(topLevelClause.ClauseIdentifier);
                }
            }

            // When relationships are requested, resolve them for all matched entities in a single
            // native join (a $lookup of the relationship documents that reference each matched entity).
            var relationshipsByEntityId = request.RelationshipsToReturn is null
                ? null
                : await this.ResolveRelationshipsByEntityAsync(
                    bsonCollection, matchedClauses.Keys, request.RelationshipsToReturn, cancellationToken).ConfigureAwait(false);

            var entities = new List<QueryEntitySnapshot>();
            foreach (var (id, identifiers) in matchedClauses)
            {
                QueryEntitySnapshot? resultEntity = null;
                if (vectorScoredEntities.TryGetValue(id, out var vectorEntity))
                {
                    resultEntity = vectorEntity with { MatchingClauseIdentifiers = identifiers.ToArray() };
                }
                else if (BuildCurrentSnapshot(documentsById[id]) is { } snapshot)
                {
                    resultEntity = snapshot with { MatchingClauseIdentifiers = identifiers.ToArray() };
                }

                if (resultEntity is null)
                {
                    continue;
                }

                if (relationshipsByEntityId is not null)
                {
                    resultEntity = resultEntity with
                    {
                        Relationships = relationshipsByEntityId.TryGetValue(id, out var entityRelationships)
                            ? entityRelationships
                            : [],
                    };
                }

                entities.Add(resultEntity);
            }

            batches.Add(new TimestampedQueryBatch { Timestamp = timestamp, Entities = entities });
        }

        return new QueryResult { Batches = batches };
    }

    /// <summary>
    /// Resolves, in a single native aggregation join, the relationships that reference each of the
    /// given entity ids (the entity appears among the relationship's participant ids), filtered by the
    /// requested relationship types. Returns a map from entity id to its matching relationship snapshots.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyCollection<EntitySnapshot>>> ResolveRelationshipsByEntityAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        IReadOnlyCollection<string> entityIds,
        IReadOnlyCollection<GetRelationshipRequest> relationshipRequests,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyCollection<EntitySnapshot>>(StringComparer.Ordinal);
        if (entityIds.Count == 0)
        {
            return result;
        }

        var entityIdArray = new BsonArray(entityIds);

        var pipeline = new List<BsonDocument>
        {
            // Non-deleted relationship documents (those carrying a participants object).
            new("$match", new BsonDocument
            {
                { MongoDbQueryFilterBuilder.IsDeletedField, new BsonDocument("$ne", true) },
                { "current.data.participants", new BsonDocument("$exists", true) },
            }),
        };

        // Restrict to the requested relationship types (unless any request omits a type filter).
        if (TryResolveRequestedRelationshipTypes(relationshipRequests, out var requestedTypes))
        {
            pipeline.Add(new BsonDocument("$match", new BsonDocument(
                MongoDbQueryFilterBuilder.EntityTypesField, new BsonDocument("$in", new BsonArray(requestedTypes)))));
        }

        // Keep only relationships referencing one of the requested entities, and emit one row per
        // (referenced entity id, relationship document).
        pipeline.Add(new BsonDocument("$project", new BsonDocument
        {
            { "owners", new BsonDocument("$setIntersection", new BsonArray { BuildRoleIdsExpression(null), entityIdArray }) },
            { "doc", "$$ROOT" },
        }));
        pipeline.Add(new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray
        {
            new BsonDocument("$size", "$owners"),
            0,
        }))));
        pipeline.Add(new BsonDocument("$unwind", "$owners"));

        var rows = await bsonCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var relationshipsByOwner = new Dictionary<string, List<EntitySnapshot>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var owner = row["owners"].AsString;
            if (BuildCurrentSnapshot(row["doc"].AsBsonDocument) is not { } relationshipSnapshot)
            {
                continue;
            }

            if (!relationshipsByOwner.TryGetValue(owner, out var ownerRelationships))
            {
                relationshipsByOwner[owner] = ownerRelationships = [];
            }

            ownerRelationships.Add(relationshipSnapshot);
        }

        foreach (var (owner, ownerRelationships) in relationshipsByOwner)
        {
            result[owner] = ownerRelationships;
        }

        return result;
    }

    /// <summary>
    /// Resolves the union of relationship type names requested across the filters; returns
    /// <see langword="false"/> when any filter omits a type restriction (so all types match).
    /// </summary>
    private static bool TryResolveRequestedRelationshipTypes(
        IReadOnlyCollection<GetRelationshipRequest> relationshipRequests,
        out string[] requestedTypes)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in relationshipRequests)
        {
            var typeFilter = request.RelationshipTypeNames?.Values;
            if (typeFilter is null || typeFilter.Length == 0)
            {
                requestedTypes = [];
                return false;
            }

            foreach (var typeName in typeFilter)
            {
                types.Add(typeName);
            }
        }

        requestedTypes = [.. types];
        return true;
    }

    /// <summary>
    /// Ensures the five required query indexes exist on the entity collection. This is idempotent and
    /// should be called once on startup before serving any queries.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var indexModels = new CreateIndexModel<MongoDbEntityDocument>[]
        {
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.EntityTypesField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.NamesField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.NameParentPrefixesField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.ParticipantIdsField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending("current.modified-time-utc")),
        };

        await _entityCollection.Indexes.CreateManyAsync(indexModels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Backfills <c>current.name-parent-prefixes</c> and <c>current.participant-ids</c> on any
    /// documents that are missing those fields (written before this schema version), and removes the
    /// obsolete <c>current.names</c> and <c>current.type-names</c> fields. Processes up to 500
    /// documents per <c>bulkWrite</c> batch. Idempotent — safe to call multiple times.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        const int BatchSize = 500;
        var bsonCollection = _entityCollection.Database.GetCollection<BsonDocument>(
            _entityCollection.CollectionNamespace.CollectionName);

        // Find all non-deleted docs that are missing the new name-parent-prefixes field.
        var filter = new BsonDocument
        {
            { MongoDbGetFilterBuilder.NameParentPrefixesField, new BsonDocument("$exists", false) },
            { MongoDbGetFilterBuilder.IsDeletedField, new BsonDocument("$ne", true) },
        };

        var cursor = await bsonCollection
            .Find(filter)
            .ToCursorAsync(cancellationToken)
            .ConfigureAwait(false);

        var batch = new List<BsonDocument>(BatchSize);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.AddRange(cursor.Current);
            while (batch.Count >= BatchSize)
            {
                await ApplyMigrationBatchAsync(bsonCollection, batch[..BatchSize], cancellationToken)
                    .ConfigureAwait(false);
                batch.RemoveRange(0, BatchSize);
            }
        }

        if (batch.Count > 0)
        {
            await ApplyMigrationBatchAsync(bsonCollection, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyMigrationBatchAsync(
        IMongoCollection<BsonDocument> collection,
        List<BsonDocument> docs,
        CancellationToken cancellationToken)
    {
        var writes = new List<WriteModel<BsonDocument>>(docs.Count);

        foreach (var doc in docs)
        {
            var id = doc["_id"];
            var current = doc["current"].AsBsonDocument;
            var data = current.Contains("data") && current["data"] is BsonDocument d ? d : null;

            // Compute name-parent-prefixes from data.names
            var prefixArray = new BsonArray();
            if (data is not null)
            {
                foreach (var nameComponents in ReadNameComponents(data))
                {
                    for (var i = 1; i < nameComponents.Length; i++)
                    {
                        prefixArray.Add(new BsonArray(nameComponents[..i].Select(s => (BsonValue)new BsonString(s))));
                    }
                }
            }

            // Compute participant-ids from data.participants
            var participantIdsArray = new BsonArray();
            if (data is not null)
            {
                var dataJson = MongoEntityData.ToJsonElement(data);
                if (RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(dataJson, out var ids))
                {
                    foreach (var id2 in ids)
                    {
                        participantIdsArray.Add(new BsonString(id2.ToString()));
                    }
                }
            }

            var update = new BsonDocument
            {
                {
                    "$set", new BsonDocument
                    {
                        { "current.name-parent-prefixes", prefixArray },
                        { "current.participant-ids", participantIdsArray },
                    }
                },
                {
                    "$unset", new BsonDocument
                    {
                        { "current.names", "" },
                        { "current.type-names", "" },
                    }
                },
            };

            writes.Add(new UpdateOneModel<BsonDocument>(
                new BsonDocumentFilterDefinition<BsonDocument>(new BsonDocument("_id", id)),
                new BsonDocumentUpdateDefinition<BsonDocument>(update)));
        }

        if (writes.Count > 0)
        {
            await collection.BulkWriteAsync(writes, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
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
                        { "path", MongoDbQueryFilterBuilder.EmbeddingField },
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

    /// <summary>
    /// Executes an <see cref="EntityParticipationQueryClause"/> as a native aggregation: it matches
    /// relationship documents of the requested type(s) (optionally requiring a participant matching
    /// the MustHave sub-clause), collects the participant ids in the requested roles, and
    /// <c>$lookup</c>-joins back to the entity collection to return the participant entity documents.
    /// Each clause in <paramref name="exclusions"/> drops result entities that are a participant (in
    /// that exclusion's role) of a relationship of the exclusion's type — a join-based
    /// <c>Not(participation)</c>, used to filter out, for example, not-interesting targets.
    /// </summary>
    private async Task<List<BsonDocument>> ExecuteParticipationClauseAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        MongoDbQueryFilterBuilder translator,
        EntityParticipationQueryClause clause,
        IReadOnlyList<EntityParticipationQueryClause> exclusions,
        CancellationToken cancellationToken)
    {
        var relationshipTypes = new BsonArray(clause.RelationshipTypeNames.Values ?? []);
        var collectionName = bsonCollection.CollectionNamespace.CollectionName;

        var pipeline = new List<BsonDocument>
        {
            // Non-deleted relationship documents carrying one of the requested types (native match).
            new("$match", new BsonDocument
            {
                { MongoDbQueryFilterBuilder.IsDeletedField, new BsonDocument("$ne", true) },
                { MongoDbQueryFilterBuilder.EntityTypesField, new BsonDocument("$in", relationshipTypes) },
            }),
        };

        // MustHave: the relationship must carry a participant (in the given roles, or any role) whose
        // entity matches the MustHave sub-clause. This is a correlated $lookup join from the
        // relationship's participant ids to the entity collection, with the translated sub-clause
        // filter applied inside the join; the relationship is kept only if the join yields a match.
        if (clause.MustHave is { } mustHave)
        {
            var mustHaveFilter = RenderFilter(translator.TranslateToFilter(mustHave.Clause));

            pipeline.Add(new BsonDocument("$lookup", new BsonDocument
            {
                { "from", collectionName },
                { "let", new BsonDocument("mustHaveIds", BuildRoleIdsExpression(mustHave.ParticipationRoleNames?.Values)) },
                {
                    "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$in", new BsonArray { "$_id", "$$mustHaveIds" }))),
                        new BsonDocument("$match", mustHaveFilter),
                    }
                },
                { "as", "__mustHaveMatches" },
            }));
            pipeline.Add(new BsonDocument("$match", new BsonDocument("__mustHaveMatches.0", new BsonDocument("$exists", true))));
        }

        // Collect the participant ids in the result roles, then join to the entity documents.
        pipeline.Add(new BsonDocument("$project", new BsonDocument("ids", BuildRoleIdsExpression(clause.ParticipationRoleNames?.Values))));
        pipeline.Add(new BsonDocument("$unwind", "$ids"));
        pipeline.Add(new BsonDocument("$lookup", new BsonDocument
        {
            { "from", collectionName },
            { "localField", "ids" },
            { "foreignField", "_id" },
            { "as", "entity" },
        }));
        pipeline.Add(new BsonDocument("$unwind", "$entity"));
        pipeline.Add(new BsonDocument("$match", new BsonDocument("entity." + MongoDbQueryFilterBuilder.IsDeletedField, new BsonDocument("$ne", true))));
        // Deduplicate participant entities (a shared participant referenced by several relationships).
        pipeline.Add(new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$entity._id" },
            { "doc", new BsonDocument("$first", "$entity") },
        }));
        pipeline.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$doc")));

        // Exclusions (join-based Not(participation)): drop result entities that are a participant, in
        // the exclusion's role(s), of a non-deleted relationship of the exclusion's type(s). Each is a
        // correlated $lookup whose sub-pipeline finds such relationships for the entity; the entity is
        // kept only when the lookup returns nothing.
        for (var exclusionIndex = 0; exclusionIndex < exclusions.Count; exclusionIndex++)
        {
            var exclusion = exclusions[exclusionIndex];
            var exclusionField = "__excluded" + exclusionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var exclusionTypes = new BsonArray(exclusion.RelationshipTypeNames.Values ?? []);

            pipeline.Add(new BsonDocument("$lookup", new BsonDocument
            {
                { "from", collectionName },
                { "let", new BsonDocument("entityId", "$_id") },
                {
                    "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$ne", new BsonArray { "$" + MongoDbQueryFilterBuilder.IsDeletedField, true }),
                            new BsonDocument("$gt", new BsonArray
                            {
                                new BsonDocument("$size", new BsonDocument("$setIntersection", new BsonArray
                                {
                                    new BsonDocument("$ifNull", new BsonArray { "$" + MongoDbQueryFilterBuilder.EntityTypesField, new BsonArray() }),
                                    exclusionTypes,
                                })),
                                0,
                            }),
                            new BsonDocument("$in", new BsonArray { "$$entityId", BuildRoleIdsExpression(exclusion.ParticipationRoleNames?.Values) }),
                        }))),
                    }
                },
                { "as", exclusionField },
            }));
            pipeline.Add(new BsonDocument("$match", new BsonDocument(exclusionField, new BsonDocument("$size", 0))));
        }

        return await bsonCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a transit clause: finds source entities matching the MatchClause, then traverses
    /// relationships to find destination entities. Implemented as a single native MongoDB aggregation
    /// with joins: source entities -> relationships carrying source -> destination entities.
    /// </summary>
    private async Task<List<BsonDocument>> ExecuteTransitClauseAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        MongoDbQueryFilterBuilder translator,
        TransitQueryClause clause,
        CancellationToken cancellationToken)
    {
        var relationshipTypes = new BsonArray(clause.RelationshipTypeNames.Values ?? []);
        var collectionName = bsonCollection.CollectionNamespace.CollectionName;

        // Find source entities that match the MatchClause.
        var sourceFilter = RenderFilter(translator.TranslateToFilter(clause.MatchClause));

        var pipeline = new List<BsonDocument>
        {
            // Start with source entities matching the clause.
            new("$match", sourceFilter),
            // Join to relationships where this source entity participates in the source role(s).
            new("$lookup", new BsonDocument
            {
                { "from", collectionName },
                { "let", new BsonDocument("sourceEntityId", "$_id") },
                {
                    "pipeline", new BsonArray
                    {
                        // Relationship must be non-deleted and of the requested type.
                        new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$ne", new BsonArray { "$" + MongoDbQueryFilterBuilder.IsDeletedField, true }),
                            new BsonDocument("$gt", new BsonArray
                            {
                                new BsonDocument("$size", new BsonDocument("$setIntersection", new BsonArray
                                {
                                    new BsonDocument("$ifNull", new BsonArray { "$" + MongoDbQueryFilterBuilder.EntityTypesField, new BsonArray() }),
                                    relationshipTypes,
                                })),
                                0,
                            }),
                            // Relationship must have the source entity in the source role(s).
                            new BsonDocument("$in", new BsonArray { "$$sourceEntityId", BuildRoleIdsExpression(clause.SourceParticipationRoleNames?.Values) }),
                        }))),
                        // Project the destination participant ids.
                        new BsonDocument("$project", new BsonDocument("destIds", BuildRoleIdsExpression(clause.DestinationParticipationRoleNames?.Values))),
                    }
                },
                { "as", "relationships" },
            }),
            // Flatten relationships array and unwind destination ids.
            new("$unwind", "$relationships"),
            new("$project", new BsonDocument("destIds", "$relationships.destIds")),
            new("$unwind", "$destIds"),
            // Join to destination entities.
            new("$lookup", new BsonDocument
            {
                { "from", collectionName },
                { "localField", "destIds" },
                { "foreignField", "_id" },
                { "as", "destEntity" },
            }),
            new("$unwind", "$destEntity"),
            new("$match", new BsonDocument("destEntity." + MongoDbQueryFilterBuilder.IsDeletedField, new BsonDocument("$ne", true))),
            // Deduplicate destination entities (a shared destination referenced by several relationships).
            new("$group", new BsonDocument
            {
                { "_id", "$destEntity._id" },
                { "doc", new BsonDocument("$first", "$destEntity") },
            }),
            new("$replaceRoot", new BsonDocument("newRoot", "$doc")),
        };

        return await bsonCollection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a non-vector top-level clause to its matching entity documents. Participation clauses
    /// — bare, or composed as <c>And(participation, Not(participation), ...)</c> — are evaluated as a
    /// single native aggregation join (the negated participations become exclusions). Filter clauses
    /// (entity-type, field) composed with <c>Not(participation)</c> exclusions are executed as a filter
    /// followed by participation-based anti-joins. Any other clause is translated to a find filter.
    /// </summary>
    private async Task<List<BsonDocument>> ExecuteEntityClauseAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        MongoDbQueryFilterBuilder translator,
        QueryClause clause,
        CancellationToken cancellationToken)
    {
        if (clause is TransitQueryClause transitClause)
        {
            return await this.ExecuteTransitClauseAsync(
                bsonCollection, translator, transitClause, cancellationToken).ConfigureAwait(false);
        }

        if (TryDecomposeParticipation(clause, out var positiveParticipation, out var exclusions))
        {
            return await this.ExecuteParticipationClauseAsync(
                bsonCollection, translator, positiveParticipation, exclusions, cancellationToken).ConfigureAwait(false);
        }

        if (TryDecomposeFilterWithParticipationExclusions(clause, out var filterClause, out var participationExclusions))
        {
            return await this.ExecuteFilterWithParticipationExclusionsAsync(
                bsonCollection, translator, filterClause, participationExclusions, cancellationToken).ConfigureAwait(false);
        }

        var filter = translator.TranslateToFilter(clause);
        var find = bsonCollection.Find(filter);
        if (MongoDbQueryFilterBuilder.GetResultLimit(clause) is { } limit && limit >= 0)
        {
            find = find.Limit(limit);
        }

        return await find.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes a participation clause, optionally composed via <c>And</c> with one or more
    /// <c>Not(participation)</c> exclusions. Returns <see langword="false"/> for any other shape so the
    /// caller falls back to filter translation.
    /// </summary>
    private static bool TryDecomposeParticipation(
        QueryClause clause,
        out EntityParticipationQueryClause positiveParticipation,
        out IReadOnlyList<EntityParticipationQueryClause> exclusions)
    {
        positiveParticipation = null!;
        exclusions = [];

        if (clause is EntityParticipationQueryClause bareParticipation)
        {
            positiveParticipation = bareParticipation;
            return true;
        }

        if (clause is AndQueryClause andClause)
        {
            EntityParticipationQueryClause? positive = null;
            var negated = new List<EntityParticipationQueryClause>();
            foreach (var subClause in andClause.Clauses)
            {
                if (subClause is EntityParticipationQueryClause participation && positive is null)
                {
                    positive = participation;
                }
                else if (subClause is NotQueryClause { Clause: EntityParticipationQueryClause negatedParticipation })
                {
                    negated.Add(negatedParticipation);
                }
                else
                {
                    return false;
                }
            }

            if (positive is not null)
            {
                positiveParticipation = positive;
                exclusions = negated;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recognizes <c>And(filter-clause, Not(participation), ...)</c> where the filter clause is translatable
    /// (entity-type, field, and/or, etc.) and one or more <c>Not(participation)</c> exclusions follow.
    /// Returns <see langword="true"/> if the pattern matches so the caller can execute the filter and apply
    /// participation-based anti-joins.
    /// </summary>
    private static bool TryDecomposeFilterWithParticipationExclusions(
        QueryClause clause,
        out QueryClause filterClause,
        out IReadOnlyList<EntityParticipationQueryClause> exclusions)
    {
        filterClause = null!;
        exclusions = [];

        if (clause is not AndQueryClause andClause)
        {
            return false;
        }

        var negated = new List<EntityParticipationQueryClause>();
        var filterClauses = new List<QueryClause>();

        foreach (var subClause in andClause.Clauses)
        {
            if (subClause is NotQueryClause { Clause: EntityParticipationQueryClause negatedParticipation })
            {
                negated.Add(negatedParticipation);
            }
            else
            {
                filterClauses.Add(subClause);
            }
        }

        // Must have at least one participation exclusion and at least one filter clause.
        if (negated.Count == 0 || filterClauses.Count == 0)
        {
            return false;
        }

        // Combine filter clauses into a single clause (unwrap if only one).
        filterClause = filterClauses.Count == 1
            ? filterClauses[0]
            : new AndQueryClause { Clauses = filterClauses };
        exclusions = negated;
        return true;
    }

    /// <summary>
    /// Executes a filter clause (entity-type, field, etc.) to find matching entities, then applies
    /// participation-based anti-joins to exclude entities that participate in the specified relationships.
    /// This handles the <c>And(filter-clause, Not(participation), ...)</c> pattern commonly produced by
    /// <see cref="NotInterestingQuery"/> wrapping view queries.
    /// </summary>
    private async Task<List<BsonDocument>> ExecuteFilterWithParticipationExclusionsAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        MongoDbQueryFilterBuilder translator,
        QueryClause filterClause,
        IReadOnlyList<EntityParticipationQueryClause> exclusions,
        CancellationToken cancellationToken)
    {
        // Execute the filter to get matching entity documents.
        var filter = translator.TranslateToFilter(filterClause);
        var find = bsonCollection.Find(filter);
        if (MongoDbQueryFilterBuilder.GetResultLimit(filterClause) is { } limit && limit >= 0)
        {
            find = find.Limit(limit);
        }

        var matchedDocuments = await find.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Apply participation exclusions as anti-joins: for each exclusion, remove entities that
        // participate in the specified relationship/roles.
        foreach (var exclusion in exclusions)
        {
            var excludedIds = await this.GetParticipatingEntityIdsAsync(
                bsonCollection, exclusion, cancellationToken).ConfigureAwait(false);

            matchedDocuments.RemoveAll(doc => excludedIds.Contains(doc["_id"].AsString));
        }

        return matchedDocuments;
    }

    /// <summary>
    /// Returns the entity IDs that participate in the specified relationship types and roles.
    /// Used for anti-join exclusions.
    /// </summary>
    private async Task<HashSet<string>> GetParticipatingEntityIdsAsync(
        IMongoCollection<BsonDocument> bsonCollection,
        EntityParticipationQueryClause clause,
        CancellationToken cancellationToken)
    {
        var relationshipTypes = new BsonArray(clause.RelationshipTypeNames.Values ?? []);
        var roleNames = clause.ParticipationRoleNames?.Values ?? [];

        // Match relationship documents of the requested types.
        var pipeline = new List<BsonDocument>
        {
            new("$match", new BsonDocument
            {
                { MongoDbQueryFilterBuilder.IsDeletedField, new BsonDocument("$ne", true) },
                { MongoDbQueryFilterBuilder.EntityTypesField, new BsonDocument("$in", relationshipTypes) },
            }),
        };

        // Collect participant IDs from the specified roles (or all roles if none specified).
        var participantIdsExpression = BuildRoleIdsExpression(roleNames);
        pipeline.Add(new BsonDocument("$project", new BsonDocument("participantIds", participantIdsExpression)));
        pipeline.Add(new BsonDocument("$unwind", "$participantIds"));
        pipeline.Add(new BsonDocument("$group", new BsonDocument("_id", "$participantIds")));

        var result = await bsonCollection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken).ConfigureAwait(false);
        var participantIds = new HashSet<string>(StringComparer.Ordinal);
        while (await result.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var doc in result.Current)
            {
                participantIds.Add(doc["_id"].AsString);
            }
        }

        return participantIds;
    }

    private static BsonDocument RenderFilter(FilterDefinition<BsonDocument> filter)
        => filter.Render(new RenderArgs<BsonDocument>(
            global::MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            global::MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry));

    /// <summary>
    /// Builds an aggregation expression yielding the flat array of participant ids for the given roles
    /// (all roles when <paramref name="roleNames"/> is null/empty), normalizing each role value (a
    /// single id or an array of ids) into the array.
    /// </summary>
    private static BsonDocument BuildRoleIdsExpression(string[]? roleNames)
    {
        // participants as an array of { k: roleName, v: roleValue } entries.
        BsonValue entries = new BsonDocument("$objectToArray",
            new BsonDocument("$ifNull", new BsonArray { "$current.data.participants", new BsonDocument() }));

        if (roleNames is { Length: > 0 })
        {
            entries = new BsonDocument("$filter", new BsonDocument
            {
                { "input", entries },
                { "as", "entry" },
                { "cond", new BsonDocument("$in", new BsonArray { "$$entry.k", new BsonArray(roleNames) }) },
            });
        }

        return new BsonDocument("$reduce", new BsonDocument
        {
            { "input", entries },
            { "initialValue", new BsonArray() },
            {
                "in", new BsonDocument("$concatArrays", new BsonArray
                {
                    "$$value",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$isArray", "$$this.v"),
                        "$$this.v",
                        new BsonArray { "$$this.v" },
                    }),
                })
            },
        });
    }

    private static QueryEntitySnapshot? BuildCurrentSnapshot(BsonDocument document)
    {
        if (!document.TryGetValue("current", out var currentValue) || currentValue is not BsonDocument current)
        {
            return null;
        }

        if (!current.TryGetValue("data", out var dataValue) || dataValue.IsBsonNull)
        {
            return null;
        }

        var modifiedVersion = current.GetValue("modified-version", BsonNull.Value);
        var modifiedTimeUtc = current.GetValue("modified-time-utc", BsonNull.Value);
        if (modifiedVersion.IsBsonNull || modifiedTimeUtc.IsBsonNull)
        {
            return null;
        }

        var versionId = modifiedVersion.AsString;
        var modifiedTime = new Timestamp(new DateTimeOffset(modifiedTimeUtc.ToUniversalTime(), TimeSpan.Zero), versionId);

        return new QueryEntitySnapshot
        {
            EntityId = new EntityId(document["_id"].AsString),
            ConcurrencyTag = new ConcurrencyTag(versionId),
            ModifiedTime = modifiedTime,
            Data = MongoEntityData.ToJsonElement(dataValue),
            Relationships = [],
            MatchingClauseIdentifiers = [],
        };
    }

    public async Task<ProcessQueueResult> ProcessQueueAsync(
        ProcessQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Token is { } acknowledgedToken)
        {
            await _queueHeadCollection.ReplaceOneAsync(
                Builders<MongoDbQueueHead>.Filter.Eq(head => head.Id, request.QueueName),
                new MongoDbQueueHead
                {
                    Id = request.QueueName,
                    ModifiedTimeUtc = acknowledgedToken.DateTime.UtcDateTime,
                    ModifiedVersion = acknowledgedToken.ChangeId,
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }

        var persistedHead = await _queueHeadCollection
            .Find(Builders<MongoDbQueueHead>.Filter.Eq(head => head.Id, request.QueueName))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var bsonCollection = _entityCollection.Database.GetCollection<BsonDocument>(
            _entityCollection.CollectionNamespace.CollectionName);

        var filter = Builders<BsonDocument>.Filter.Empty;
        if (persistedHead is not null)
        {
            // Entities strictly after the head, ordered by (modified-time-utc, modified-version).
            var headUtc = persistedHead.ModifiedTimeUtc;
            var headVersion = persistedHead.ModifiedVersion;
            filter = Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Gt("current.modified-time-utc", headUtc),
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("current.modified-time-utc", headUtc),
                    Builders<BsonDocument>.Filter.Gt("current.modified-version", headVersion)));
        }

        var sort = Builders<BsonDocument>.Sort
            .Ascending("current.modified-time-utc")
            .Ascending("current.modified-version");

        var documents = await bsonCollection
            .Find(filter)
            .Sort(sort)
            .Limit(Math.Max(0, request.Count))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var entities = new List<EntitySnapshot>();
        Timestamp? nextToken = persistedHead is null
            ? null
            : new Timestamp(new DateTimeOffset(persistedHead.ModifiedTimeUtc, TimeSpan.Zero), persistedHead.ModifiedVersion);
        foreach (var document in documents)
        {
            var snapshot = BuildQueueSnapshot(document);
            if (snapshot is null)
            {
                continue;
            }

            entities.Add(snapshot);
            nextToken = snapshot.ModifiedTime;
        }

        return new ProcessQueueResult { Entities = entities, Token = nextToken };
    }

    public async Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(
        ComputeEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputs = request.Entities
            .Select(entity => new Phantom.Workspaces.Data.Vector.EmbeddingInput
            {
                EntityId = entity.EntityId,
                Text = Phantom.Workspaces.Data.Vector.EntityTextProjection.ProjectText(entity.Data),
            })
            .ToArray();

        var embeddings = await _embeddingsProvider.ComputeAsync(inputs, cancellationToken).ConfigureAwait(false);

        return new ComputeEmbeddingsResult
        {
            Embeddings = embeddings
                .Select(embedding => new EntityEmbedding { EntityId = embedding.EntityId, Values = embedding.Values })
                .ToArray(),
        };
    }

    public async Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(
        UpdateEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var update in request.Updates)
        {
            var filter = Builders<MongoDbEntityDocument>.Filter.Eq(document => document.Id, update.EntityId.Value.ToString());
            var embeddingUpdate = update.Values is null
                ? Builders<MongoDbEntityDocument>.Update.Unset("current.embedding")
                : Builders<MongoDbEntityDocument>.Update.Set("current.embedding", update.Values.ToArray());

            await _entityCollection.UpdateOneAsync(filter, embeddingUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new UpdateEmbeddingsResult { Success = true };
    }

    /// <summary>
    /// Builds an entity snapshot for the queue, including tombstoned (deleted) entities with a null
    /// <see cref="EntitySnapshot.Data"/> so the indexer can clear their embeddings.
    /// </summary>
    private static EntitySnapshot? BuildQueueSnapshot(BsonDocument document)
    {
        if (!document.TryGetValue("current", out var currentValue) || currentValue is not BsonDocument current)
        {
            return null;
        }

        var modifiedVersion = current.GetValue("modified-version", BsonNull.Value);
        var modifiedTimeUtc = current.GetValue("modified-time-utc", BsonNull.Value);
        if (modifiedVersion.IsBsonNull || modifiedTimeUtc.IsBsonNull)
        {
            return null;
        }

        var dataValue = current.GetValue("data", BsonNull.Value);
        var versionId = modifiedVersion.AsString;

        return new EntitySnapshot
        {
            EntityId = new EntityId(document["_id"].AsString),
            ConcurrencyTag = new ConcurrencyTag(versionId),
            ModifiedTime = new Timestamp(new DateTimeOffset(modifiedTimeUtc.ToUniversalTime(), TimeSpan.Zero), versionId),
            Data = dataValue.IsBsonNull ? null : MongoEntityData.ToJsonElement(dataValue),
            Relationships = [],
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
                    Data = tuple.Version.Data is null ? null : MongoEntityData.ToJsonElement(tuple.Version.Data),
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
                Entity = currentVersion is null || currentVersion.Data is null
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
        BsonDocument? currentData,
        JsonElement? nextData)
    {
        if (currentData is null || nextData is null)
        {
            return currentData is null && nextData is null;
        }

        return JsonElement.DeepEquals(MongoEntityData.ToJsonElement(currentData), nextData.Value);
    }

    private static string[] ExtractParticipantIds(JsonElement? data)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(data.Value, out var ids)
            ? ids.Select(static id => id.ToString()).ToArray()
            : [];
    }

    private static string[][] ComputeNameParentPrefixes(BsonDocument data)
    {
        var prefixes = new List<string[]>();
        foreach (var nameComponents in ReadNameComponents(data))
        {
            // Store all proper prefixes (length 1 to length-1), not the empty prefix or full name.
            for (var i = 1; i < nameComponents.Length; i++)
            {
                prefixes.Add(nameComponents[..i]);
            }
        }

        return prefixes.ToArray();
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
                    if (version is null || version.Data is null)
                    {
                        return false;
                    }

                    if (requestedTypes is not null && requestedTypes.Length > 0)
                    {
                        return ReadTypeNames(version.Data).Intersect(requestedTypes, StringComparer.Ordinal).Any();
                    }

                    return true;
                }).ToArray();
        }

        var requestedName = request.EntityName.Value.Components;

        return allDocuments.Where(document =>
        {
            var version = document.Versions.LastOrDefault();
            if (version is null || version.Data is null)
            {
                return false;
            }

            if (requestedTypes is not null && requestedTypes.Length > 0)
            {
                if (!ReadTypeNames(version.Data).Intersect(requestedTypes, StringComparer.Ordinal).Any())
                {
                    return false;
                }
            }

            foreach (var components in ReadNameComponents(version.Data))
            {
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

    /// <summary>Reads the entity-type names (merging <c>type-names</c> and <c>entity-types</c>) from version BSON data.</summary>
    private static IReadOnlyCollection<string> ReadTypeNames(BsonDocument data)
    {
        var typeNames = new List<string>();
        foreach (var field in new[] { "type-names", "entity-types" })
        {
            if (data.TryGetValue(field, out var value) && value is BsonArray array)
            {
                typeNames.AddRange(array.Where(item => item.IsString).Select(item => item.AsString));
            }
        }

        return typeNames;
    }

    /// <summary>Reads each entity name as its string components from version BSON data.</summary>
    private static IEnumerable<string[]> ReadNameComponents(BsonDocument data)
    {
        if (!data.TryGetValue("names", out var namesValue) || namesValue is not BsonArray names)
        {
            yield break;
        }

        foreach (var entry in names)
        {
            if (entry is BsonArray componentArray)
            {
                yield return componentArray.Where(component => component.IsString).Select(component => component.AsString).ToArray();
            }
            else if (entry.IsString)
            {
                yield return [entry.AsString];
            }
        }
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
            if (version is null || version.Data is null)
            {
                continue;
            }

            var data = MongoEntityData.ToJsonElement(version.Data);
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
                Data = data,
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
            || participants.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Participants are role-keyed (e.g. { "target": id, "user": id } or { "entities": [id, ...] });
        // each role value is a single entity id or an array of ids. Collect every referenced id.
        foreach (var role in participants.EnumerateObject())
        {
            CollectParticipantIds(role.Value, participantIds);
        }

        return participantIds.Count > 0;
    }

    private static void CollectParticipantIds(JsonElement value, HashSet<EntityId> participantIds)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when Guid.TryParse(value.GetString(), out var guid):
                participantIds.Add(new EntityId(guid));
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CollectParticipantIds(item, participantIds);
                }

                break;
        }
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
            Data = version.Data is null ? null : MongoEntityData.ToJsonElement(version.Data),
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

    [BsonIgnoreExtraElements]
    private sealed class MongoDbCurrentProjection
    {
        /// <summary>The current version's entity data as native BSON, for native field/participant querying.</summary>
        [BsonElement("data")]
        [BsonIgnoreIfNull]
        public BsonDocument? Data { get; init; }

        /// <summary>
        /// Flat list of all participant entity IDs extracted from the <c>participants</c> object.
        /// Enables efficient relationship lookup via a multikey index on <c>current.participant-ids</c>.
        /// </summary>
        [BsonElement("participant-ids")]
        public string[] ParticipantIds { get; init; } = [];

        /// <summary>
        /// All proper prefix sub-arrays for every entity name. For a name <c>["a","b","c"]</c> this
        /// stores <c>["a"]</c> and <c>["a","b"]</c>. Enables efficient child/descendant queries via a
        /// multikey index on <c>current.name-parent-prefixes</c>.
        /// </summary>
        [BsonElement("name-parent-prefixes")]
        public string[][] NameParentPrefixes { get; init; } = [];

        [BsonElement("embedding")]
        [BsonIgnoreIfNull]
        public float[]? Embedding { get; init; }

        [BsonElement("is-deleted")]
        public bool IsDeleted { get; init; }

        [BsonElement("modified-time-utc")]
        public DateTime ModifiedTimeUtc { get; init; }

        [BsonElement("modified-version")]
        public string ModifiedVersion { get; init; } = string.Empty;
    }

    private sealed class MongoDbEntityVersion
    {
        public ObjectId VersionId { get; init; }

        public DateTime TimestampUtc { get; init; }

        /// <summary>The entity data as native BSON; null for a tombstone (delete).</summary>
        [BsonElement("data")]
        [BsonIgnoreIfNull]
        public BsonDocument? Data { get; init; }
    }

    private sealed class MongoDbQueueHead
    {
        [BsonId]
        public string Id { get; init; } = string.Empty;

        [BsonElement("modified-time-utc")]
        public DateTime ModifiedTimeUtc { get; init; }

        [BsonElement("modified-version")]
        public string ModifiedVersion { get; init; } = string.Empty;
    }
}
