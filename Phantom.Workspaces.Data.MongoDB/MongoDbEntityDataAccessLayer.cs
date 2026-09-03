using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB;

public class MongoDbEntityDataAccessLayer : IDataAccessLayer
{
    /// <summary>The Atlas vector search index name over the current-version embedding field.</summary>
    public const string VectorIndexName = "entity-current-embedding-index";

    private const int VectorIndexRemovalPollAttempts = 30;
    private static readonly TimeSpan VectorIndexRemovalPollInterval = TimeSpan.FromSeconds(2);

    private readonly IMongoCollection<MongoDbEntityDocument> _entityCollection;
    // #1411: one small document per entity version lives here instead of an unbounded inline array.
    private readonly IMongoCollection<MongoDbEntityVersionDocument> _versionCollection;
    private readonly IMongoCollection<MongoDbQueueHead> _queueHeadCollection;
    private readonly Phantom.Workspaces.Data.Vector.IEmbeddingsProvider _embeddingsProvider;
    private readonly TimeProvider _timeProvider;

    public MongoDbEntityDataAccessLayer(
        IMongoDatabase database,
        string collectionName,
        Phantom.Workspaces.Data.Vector.IEmbeddingsProvider? embeddingsProvider = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("Collection name is required.", nameof(collectionName));
        }

        _entityCollection = database.GetCollection<MongoDbEntityDocument>($"{collectionName}_entities");
        _versionCollection = database.GetCollection<MongoDbEntityVersionDocument>($"{collectionName}_versions");
        _queueHeadCollection = database.GetCollection<MongoDbQueueHead>($"{collectionName}_queue_heads");
        _embeddingsProvider = embeddingsProvider ?? new Phantom.Workspaces.Data.Vector.DeterministicEmbeddingsProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<EntityUpdateResult>();
        var pendingWrites = new List<MongoDbEntityDocument>();
        var pendingVersionWrites = new List<MongoDbEntityVersionDocument>();

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
            // #1411: the concurrency tag now comes from the current document's latest-version pointer,
            // not from an inline Versions array (which no longer exists).
            var currentVersion = GetCurrentVersion(currentDocument);
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

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var nextVersionId = ObjectId.GenerateNewId(nowUtc);
            var nextTag = new ConcurrencyTag(nextVersionId.ToString());
            var hasData = change.Data is not null;
            var nextDataBson = hasData ? MongoEntityData.ToBsonDocument(change.Data!.Value) : null;

            var updatedDocument = currentDocument ?? new MongoDbEntityDocument
            {
                Id = entityId.Value.ToString(),
            };

            // #1411: append the new version as its own small document in the versions collection instead
            // of growing an inline array on the entity document (which crossed the 16 MB BSON limit).
            pendingVersionWrites.Add(new MongoDbEntityVersionDocument
            {
                VersionId = nextVersionId,
                EntityId = entityId.Value.ToString(),
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
                NameParentPrefixes = new BsonArray(nameParentPrefixes.Select(prefix => (BsonValue)new BsonArray(prefix.Select(component => (BsonValue)new BsonString(component))))),
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

        await WritePendingChangesAsync(pendingVersionWrites, pendingWrites, cancellationToken).ConfigureAwait(false);

        return new UpdateResult
        {
            EntityResults = results,
        };
    }

    /// <summary>
    /// #1411: persists new version documents and the recomputed current documents. Uses a
    /// multi-document transaction when the deployment supports it (replica set / Atlas Local) so the
    /// write is all-or-nothing; otherwise falls back to version-first ordering, so an interrupted
    /// write leaves at worst an orphan version document (reconciled by later reads/writes) and never
    /// a current pointer referencing a missing version.
    /// </summary>
    private async Task WritePendingChangesAsync(
        List<MongoDbEntityVersionDocument> versionWrites,
        List<MongoDbEntityDocument> currentWrites,
        CancellationToken cancellationToken)
    {
        if (versionWrites.Count == 0 && currentWrites.Count == 0)
        {
            return;
        }

        var client = _entityCollection.Database.Client;
        using var session = await client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            session.StartTransaction();
            await WriteVersionsThenCurrentAsync(session, versionWrites, currentWrites, cancellationToken).ConfigureAwait(false);
            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsTransactionUnsupported(ex))
        {
            try
            {
                if (session.IsInTransaction)
                {
                    await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (MongoException)
            {
                // Aborting a transaction that never began (standalone deployment) is expected here.
            }
        }

        // Version-first ordering fallback for deployments without multi-document transactions.
        await WriteVersionsThenCurrentAsync(null, versionWrites, currentWrites, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteVersionsThenCurrentAsync(
        IClientSessionHandle? session,
        List<MongoDbEntityVersionDocument> versionWrites,
        List<MongoDbEntityDocument> currentWrites,
        CancellationToken cancellationToken)
    {
        // Insert versions first so a crash between the two writes can only orphan a version document,
        // never lose the version a current pointer references.
        if (versionWrites.Count > 0)
        {
            if (session is null)
            {
                await _versionCollection.InsertManyAsync(versionWrites, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _versionCollection.InsertManyAsync(session, versionWrites, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var currentWrite in currentWrites)
        {
            var filter = Builders<MongoDbEntityDocument>.Filter.Eq(static document => document.Id, currentWrite.Id);
            var options = new ReplaceOptions { IsUpsert = true };
            if (session is null)
            {
                await _entityCollection.ReplaceOneAsync(filter, currentWrite, options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _entityCollection.ReplaceOneAsync(session, filter, currentWrite, options, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransactionUnsupported(Exception exception)
    {
        if (exception is NotSupportedException)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("Transaction numbers are only allowed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Transactions are not supported", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("Transaction", StringComparison.OrdinalIgnoreCase)
                && message.Contains("replica set", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #1411: builds an in-memory version view of the current document from its latest-version pointer
    /// (the inline Versions array no longer exists). Returns <see langword="null"/> for a brand-new
    /// entity with no current projection.
    /// </summary>
    private static MongoDbEntityVersion? GetCurrentVersion(MongoDbEntityDocument? document)
    {
        var current = document?.Current;
        if (current is null || string.IsNullOrEmpty(current.ModifiedVersion))
        {
            return null;
        }

        return new MongoDbEntityVersion
        {
            VersionId = ObjectId.TryParse(current.ModifiedVersion, out var parsed) ? parsed : ObjectId.Empty,
            TimestampUtc = current.ModifiedTimeUtc,
            Data = current.Data,
        };
    }

    public virtual async Task<GetResult> GetAsync(
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

        // #1412: history now lives in the versions collection; the current document is read directly
        // and no longer needs an inline version bootstrapped for the read path.
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
                    // #1412: a null timestamp reads the denormalized current projection directly; an
                    // at-timestamp read resolves the bracketing version from the versions collection.
                    EntitySnapshot? snapshot;
                    if (timestamp is null)
                    {
                        snapshot = CreateSnapshotFromCurrent(match);
                    }
                    else
                    {
                        var version = await ResolveVersionAtTimestampAsync(match.Id, timestamp.Value, cancellationToken)
                            .ConfigureAwait(false);
                        snapshot = version is null ? null : CreateSnapshot(new EntityId(match.Id), version);
                    }

                    if (snapshot is null)
                    {
                        continue;
                    }

                    var entityId = new EntityId(match.Id);
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
    /// Ensures the required query indexes exist on the entity collection. This is idempotent and
    /// should be called once on startup before serving any queries.
    /// </summary>
    public virtual async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var indexModels = new CreateIndexModel<MongoDbEntityDocument>[]
        {
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.EntityTypesField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.NamesField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.NameParentPrefixesField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending(MongoDbGetFilterBuilder.ParticipantIdsField)),
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending("current.modified-time-utc")),
            // #1360: supports server-side sort-by + top-N on tool-execution-result history (start-time).
            new(Builders<MongoDbEntityDocument>.IndexKeys.Ascending("current.data.start-time")),
        };

        await _entityCollection.Indexes.CreateManyAsync(indexModels, cancellationToken).ConfigureAwait(false);

        // #1411: indexes on the versions collection supporting point-in-time/history resolution
        // (EntityId + TimestampUtc + _id) and export/changed-entities streaming (TimestampUtc + _id).
        var versionIndexModels = new CreateIndexModel<MongoDbEntityVersionDocument>[]
        {
            new(Builders<MongoDbEntityVersionDocument>.IndexKeys
                .Ascending(static version => version.EntityId)
                .Ascending(static version => version.TimestampUtc)
                .Ascending(static version => version.VersionId)),
            new(Builders<MongoDbEntityVersionDocument>.IndexKeys
                .Ascending(static version => version.TimestampUtc)
                .Ascending(static version => version.VersionId)),
        };

        await _versionCollection.Indexes.CreateManyAsync(versionIndexModels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Backfills <c>current.name-parent-prefixes</c> and <c>current.participant-ids</c> on any
    /// documents that are missing those fields (written before this schema version), removes the
    /// obsolete <c>current.names</c> and <c>current.type-names</c> fields, and (#1413) performs the
    /// one-shot migration of legacy inline <c>Versions</c> arrays into the
    /// <c>{collectionName}_versions</c> collection. Processes up to 500 documents per batch.
    /// Idempotent and crash-safe — safe to call on every startup.
    /// </summary>
    public virtual async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        const int BatchSize = 500;
        var bsonCollection = _entityCollection.Database.GetCollection<BsonDocument>(
            _entityCollection.CollectionNamespace.CollectionName);

        // #1413: move legacy inline versions into the versions collection and shrink oversized docs.
        await MigrateInlineVersionsAsync(bsonCollection, BatchSize, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// #1413: one-shot, idempotent, crash-safe migration of legacy inline <c>Versions</c> arrays into
    /// the <c>{collectionName}_versions</c> collection. For each old-shape document, every inline
    /// version is upserted (keyed by its VersionId) into the versions collection, then the inline
    /// array is <c>$unset</c> — shrinking documents that had grown near the 16 MB BSON limit. If no
    /// document carries an inline <c>Versions</c> array, this is a no-op.
    /// </summary>
    private async Task MigrateInlineVersionsAsync(
        IMongoCollection<BsonDocument> collection,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // Old-shape documents are exactly those that still carry an inline Versions array.
        var oldShapeFilter = new BsonDocument("Versions", new BsonDocument("$exists", true));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Project only _id so a single oversized (16 MB) document is never loaded whole here.
            var idDocuments = await collection
                .Find(oldShapeFilter)
                .Project(new BsonDocument("_id", 1))
                .Limit(batchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (idDocuments.Count == 0)
            {
                return;
            }

            foreach (var idDocument in idDocuments)
            {
                await MigrateEntityInlineVersionsAsync(collection, idDocument["_id"], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task MigrateEntityInlineVersionsAsync(
        IMongoCollection<BsonDocument> collection,
        BsonValue entityId,
        CancellationToken cancellationToken)
    {
        // Oversized-document rescue: read the inline Versions array in $slice windows so even a
        // document at the 16 MB ceiling can be migrated without loading it whole.
        const int VersionSliceSize = 200;
        var entityIdString = entityId.IsString ? entityId.AsString : entityId.ToString() ?? string.Empty;
        var skip = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projection = new BsonDocument("Versions", new BsonDocument("$slice", new BsonArray { skip, VersionSliceSize }));
            var sliced = await collection
                .Find(new BsonDocument("_id", entityId))
                .Project(projection)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (sliced is null
                || !sliced.TryGetValue("Versions", out var versionsValue)
                || versionsValue is not BsonArray versions
                || versions.Count == 0)
            {
                break;
            }

            var writes = new List<WriteModel<MongoDbEntityVersionDocument>>(versions.Count);
            foreach (var versionValue in versions)
            {
                if (versionValue is not BsonDocument version)
                {
                    continue;
                }

                var versionDocument = new MongoDbEntityVersionDocument
                {
                    VersionId = ReadInlineVersionId(version),
                    EntityId = entityIdString,
                    TimestampUtc = ReadInlineVersionTimestamp(version, _timeProvider),
                    Data = version.TryGetValue("data", out var dataValue) && dataValue is BsonDocument dataDocument
                        ? dataDocument
                        : null,
                };

                // Upsert keyed by VersionId so re-running the migration never duplicates a version.
                writes.Add(new ReplaceOneModel<MongoDbEntityVersionDocument>(
                    Builders<MongoDbEntityVersionDocument>.Filter.Eq(static v => v.VersionId, versionDocument.VersionId),
                    versionDocument)
                {
                    IsUpsert = true,
                });
            }

            if (writes.Count > 0)
            {
                await _versionCollection.BulkWriteAsync(writes, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            skip += versions.Count;
            if (versions.Count < VersionSliceSize)
            {
                break;
            }
        }

        // Copy-then-$unset: only after every version has been upserted do we drop the inline array, so
        // an interrupted run leaves the (still old-shape) document to be re-migrated on next startup.
        await collection.UpdateOneAsync(
            new BsonDocument("_id", entityId),
            new BsonDocument("$unset", new BsonDocument("Versions", string.Empty)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static ObjectId ReadInlineVersionId(BsonDocument version)
    {
        if (version.TryGetValue("VersionId", out var value))
        {
            if (value.IsObjectId)
            {
                return value.AsObjectId;
            }

            if (value.IsString && ObjectId.TryParse(value.AsString, out var parsed))
            {
                return parsed;
            }
        }

        return ObjectId.GenerateNewId();
    }

    private static DateTime ReadInlineVersionTimestamp(BsonDocument version, TimeProvider timeProvider)
    {
        if (version.TryGetValue("TimestampUtc", out var value))
        {
            if (value.IsValidDateTime)
            {
                return value.ToUniversalTime();
            }

            if (value.IsString && DateTime.TryParse(value.AsString, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return timeProvider.GetUtcNow().UtcDateTime;
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

            await Task.Delay(VectorIndexRemovalPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
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
        if (MongoDbQueryFilterBuilder.BuildSort(clause) is { } sort)
        {
            // Sort BEFORE limiting so the limit is a true top-N-by-order (mirrors ProcessQueueAsync),
            // not an arbitrary subset.
            find = find.Sort(sort);
        }

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

        var existing = await LoadEntitiesByIdAsync(request.EntityIds, cancellationToken).ConfigureAwait(false);
        var ids = request.EntityIds.Select(static entityId => entityId.ToString()).ToArray();

        // #1412: change times come from the versions collection, ordered by (TimestampUtc, VersionId).
        var filter = Builders<MongoDbEntityVersionDocument>.Filter.In(static version => version.EntityId, ids);
        var sort = Builders<MongoDbEntityVersionDocument>.Sort
            .Ascending(static version => version.TimestampUtc)
            .Ascending(static version => version.VersionId);
        var versionDocuments = await _versionCollection
            .Find(filter)
            .Sort(sort)
            .Project(Builders<MongoDbEntityVersionDocument>.Projection.Expression(static version => new VersionTime(
                version.EntityId,
                version.TimestampUtc,
                version.VersionId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var timesByEntity = versionDocuments
            .GroupBy(static version => version.EntityId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static version => new Timestamp(
                        new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero),
                        version.VersionId.ToString()))
                    .ToArray(),
                StringComparer.Ordinal);

        var history = request.EntityIds
            .Where(entityId => existing.ContainsKey(entityId.ToString()))
            .Select(entityId => new EntityHistoryEntry
            {
                EntityId = entityId,
                UpdateTimes = timesByEntity.TryGetValue(entityId.ToString(), out var times) ? times : [],
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
        var snapshotTime = request.SnapshotTime?.DateTime.UtcDateTime;

        // #1412: stream the versions collection ordered by (TimestampUtc, VersionId), filtered by the
        // snapshot time when supplied, instead of flattening inline arrays in-process.
        var filter = snapshotTime is null
            ? Builders<MongoDbEntityVersionDocument>.Filter.Empty
            : Builders<MongoDbEntityVersionDocument>.Filter.Gte(static version => version.TimestampUtc, snapshotTime.Value);
        var sort = Builders<MongoDbEntityVersionDocument>.Sort
            .Ascending(static version => version.TimestampUtc)
            .Ascending(static version => version.VersionId);
        var versionDocuments = await _versionCollection
            .Find(filter)
            .Sort(sort)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var batches = versionDocuments.Select(version => new ExportChangeBatch
        {
            ChangeTime = new Timestamp(
                new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero),
                version.VersionId.ToString()),
            Entities =
            [
                new QueryEntitySnapshot
                {
                    EntityId = new EntityId(version.EntityId),
                    ConcurrencyTag = new ConcurrencyTag(version.VersionId.ToString()),
                    ModifiedTime = new Timestamp(
                        new DateTimeOffset(version.TimestampUtc, TimeSpan.Zero),
                        version.VersionId.ToString()),
                    Data = version.Data is null ? null : MongoEntityData.ToJsonElement(version.Data),
                    Relationships = [],
                    MatchingClauseIdentifiers = [],
                    ClassifiedTime = null,
                },
            ],
        }).ToArray();

        var finalVersion = versionDocuments.LastOrDefault();
        var finalSnapshot = finalVersion is null
            ? new Timestamp(_timeProvider.GetUtcNow(), ObjectId.GenerateNewId().ToString())
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

            // #1412: existence query against the versions collection for any change strictly after the
            // supplied (TimestampUtc, VersionId) boundary.
            var hasChangeAfter = await HasVersionAfterAsync(
                entityTimestamp.EntityId.ToString(),
                entityTimestamp.Timestamp,
                cancellationToken).ConfigureAwait(false);
            if (!hasChangeAfter)
            {
                continue;
            }

            var currentVersion = GetCurrentVersion(document);
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

    private sealed record VersionTime(string EntityId, DateTime TimestampUtc, ObjectId VersionId);

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

    // #1412: existence check against the versions collection for a change strictly after the given
    // (TimestampUtc, VersionId) boundary. Tie-break on the version id (_id ObjectId) matches the
    // in-process string.CompareOrdinal ordering used previously.
    private async Task<bool> HasVersionAfterAsync(
        string entityId,
        Timestamp timestamp,
        CancellationToken cancellationToken)
    {
        var requestedTime = timestamp.DateTime.UtcDateTime;
        var builder = Builders<MongoDbEntityVersionDocument>.Filter;

        FilterDefinition<MongoDbEntityVersionDocument> afterBoundary;
        if (ObjectId.TryParse(timestamp.ChangeId, out var changeId))
        {
            afterBoundary = builder.Or(
                builder.Gt(static version => version.TimestampUtc, requestedTime),
                builder.And(
                    builder.Eq(static version => version.TimestampUtc, requestedTime),
                    builder.Gt(static version => version.VersionId, changeId)));
        }
        else
        {
            afterBoundary = builder.Gt(static version => version.TimestampUtc, requestedTime);
        }

        var filter = builder.And(builder.Eq(static version => version.EntityId, entityId), afterBoundary);
        var match = await _versionCollection
            .Find(filter)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return match is not null;
    }

    // #1412: resolves the version bracketing a timestamp server-side, preserving the exact
    // (TimestampUtc, VersionId) ordering and tie-break (primary TimestampUtc, tie-break _id ObjectId).
    private async Task<MongoDbEntityVersion?> ResolveVersionAtTimestampAsync(
        string entityId,
        Timestamp timestamp,
        CancellationToken cancellationToken)
    {
        var requestedTime = timestamp.DateTime.UtcDateTime;
        var builder = Builders<MongoDbEntityVersionDocument>.Filter;

        FilterDefinition<MongoDbEntityVersionDocument> atOrBefore;
        if (ObjectId.TryParse(timestamp.ChangeId, out var changeId))
        {
            atOrBefore = builder.Or(
                builder.Lt(static version => version.TimestampUtc, requestedTime),
                builder.And(
                    builder.Eq(static version => version.TimestampUtc, requestedTime),
                    builder.Lte(static version => version.VersionId, changeId)));
        }
        else
        {
            atOrBefore = builder.Lt(static version => version.TimestampUtc, requestedTime);
        }

        var filter = builder.And(builder.Eq(static version => version.EntityId, entityId), atOrBefore);
        var sort = Builders<MongoDbEntityVersionDocument>.Sort
            .Descending(static version => version.TimestampUtc)
            .Descending(static version => version.VersionId);

        var versionDocument = await _versionCollection
            .Find(filter)
            .Sort(sort)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return versionDocument is null
            ? null
            : new MongoDbEntityVersion
            {
                VersionId = versionDocument.VersionId,
                TimestampUtc = versionDocument.TimestampUtc,
                Data = versionDocument.Data,
            };
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
                    // #1412: match against the denormalized current projection (latest version data).
                    var data = document.Current?.Data;
                    if (data is null)
                    {
                        return false;
                    }

                    if (requestedTypes is not null && requestedTypes.Length > 0)
                    {
                        return ReadTypeNames(data).Intersect(requestedTypes, StringComparer.Ordinal).Any();
                    }

                    return true;
                }).ToArray();
        }

        var requestedName = request.EntityName.Value.Components;

        return allDocuments.Where(document =>
        {
            var data = document.Current?.Data;
            if (data is null)
            {
                return false;
            }

            if (requestedTypes is not null && requestedTypes.Length > 0)
            {
                if (!ReadTypeNames(data).Intersect(requestedTypes, StringComparer.Ordinal).Any())
                {
                    return false;
                }
            }

            foreach (var components in ReadNameComponents(data))
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
            // #1412: relationships are resolved from the current projection (latest version data).
            var current = document.Current;
            if (current?.Data is null)
            {
                continue;
            }

            var data = MongoEntityData.ToJsonElement(current.Data);
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
                ConcurrencyTag = new ConcurrencyTag(current.ModifiedVersion),
                ModifiedTime = new Timestamp(new DateTimeOffset(current.ModifiedTimeUtc, TimeSpan.Zero), current.ModifiedVersion),
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

    // #1412: builds a snapshot straight from the current projection for null-timestamp ("now") reads,
    // without touching the versions collection.
    private static EntitySnapshot? CreateSnapshotFromCurrent(MongoDbEntityDocument document)
    {
        var current = document.Current;
        if (current is null)
        {
            return null;
        }

        var versionId = current.ModifiedVersion;
        return new EntitySnapshot
        {
            EntityId = new EntityId(document.Id),
            ConcurrencyTag = new ConcurrencyTag(versionId),
            ModifiedTime = new Timestamp(new DateTimeOffset(current.ModifiedTimeUtc, TimeSpan.Zero), versionId),
            Data = current.Data is null ? null : MongoEntityData.ToJsonElement(current.Data),
            Relationships = [],
        };
    }

    [BsonIgnoreExtraElements]
    private sealed class MongoDbEntityDocument
    {
        [BsonId]
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Denormalized projection of the latest version, used for native query-clause evaluation
        /// (see <see cref="MongoDbQueryTranslator"/>). Recomputed on every write.
        /// </summary>
        [BsonElement("current")]
        [BsonIgnoreIfNull]
        public MongoDbCurrentProjection? Current { get; set; }
    }

    /// <summary>
    /// #1411: one document per entity version, stored in the <c>{collectionName}_versions</c>
    /// collection. Keeps entity documents small (bounded) so they never approach the 16 MB BSON limit.
    /// </summary>
    [BsonIgnoreExtraElements]
    private sealed class MongoDbEntityVersionDocument
    {
        /// <summary>The version/change id; the ObjectId encodes the creation time.</summary>
        [BsonId]
        public ObjectId VersionId { get; init; }

        [BsonElement("EntityId")]
        public string EntityId { get; init; } = string.Empty;

        [BsonElement("TimestampUtc")]
        public DateTime TimestampUtc { get; init; }

        /// <summary>The entity data as native BSON; <see langword="null"/> for a tombstone (delete).</summary>
        [BsonElement("Data")]
        [BsonIgnoreIfNull]
        public BsonDocument? Data { get; init; }
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
        public BsonArray NameParentPrefixes { get; init; } = [];

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
