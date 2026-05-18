using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Offline;

/// <summary>
/// Filesystem-backed DAL implementation.
/// </summary>
public sealed class FilesystemDataAccessLayer : IDataAccessLayer
{
    private const string EntitiesDirectoryName = "entities";
    private const string EntityNamesIndexDirectoryName = "entityNames";
    private const string EntityNamePrefixesIndexDirectoryName = "entityNamePrefixes";
    private readonly object updateLock = new();
    private readonly Dictionary<EntityId, EntitySnapshot> deletedEntities = new();
    private long nextSequenceNumber;

    public FilesystemDataAccessLayer(
        string path)
    {
        this.Path = path;
        Directory.CreateDirectory(this.Path);
        this.nextSequenceNumber = this.LoadNextSequenceNumber();
    }

    public string Path { get; }

    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entities = this.EnumerateEntityIdsFromFiles()
            .Select(entityId => this.TryLoadEntitySnapshot(entityId))
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToArray();
        var latest = entities
            .OrderByDescending(static entity => entity.ModifiedTime.DateTime)
            .ThenByDescending(static entity => entity.ModifiedTime.ChangeId, StringComparer.Ordinal)
            .FirstOrDefault();
        var finalSnapshotTime = latest?.ModifiedTime ?? new Timestamp(DateTimeOffset.UnixEpoch, "0");

        return Task.FromResult(
            new ExportResult
            {
                ChangeBatches = entities
                    .Select(
                        entity => new ExportChangeBatch
                        {
                            ChangeTime = entity.ModifiedTime,
                            Entities =
                            [
                                new QueryEntitySnapshot
                                {
                                    EntityId = entity.EntityId,
                                    ConcurrencyTag = entity.ConcurrencyTag,
                                    ModifiedTime = entity.ModifiedTime,
                                    Data = entity.Data,
                                    Relationships = entity.Relationships,
                                    MatchingClauseIdentifiers = Array.Empty<QueryClauseIdentifier>(),
                                    FullTextQueryScores = Array.Empty<FullTextQueryScore>(),
                                },
                            ],
                        })
                    .ToArray(),
                FinalSnapshotTime = finalSnapshotTime,
            });
    }

    public Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamps = request.Timestamps is { Count: > 0 }
            ? request.Timestamps.ToArray()
            : new Timestamp?[] { null };
        var batches = new List<TimestampedEntityBatch>(timestamps.Length);

        foreach (var timestamp in timestamps)
        {
            var snapshots = new List<EntitySnapshot>();
            var includedEntityIds = new HashSet<EntityId>();
            foreach (var requestedEntity in request.Entities)
            {
                foreach (var entityId in this.FindMatchingEntityIds(requestedEntity))
                {
                    if (!includedEntityIds.Add(entityId))
                    {
                        continue;
                    }

                    var snapshot = this.TryLoadEntitySnapshot(entityId);
                    if (snapshot is null)
                    {
                        continue;
                    }

                    if (timestamp is not null
                        && this.CompareTimestamp(snapshot.ModifiedTime, timestamp.Value) > 0)
                    {
                        continue;
                    }

                    var relationshipFilter = requestedEntity.RelationshipsToReturn ?? request.RelationshipsToReturn;
                    snapshots.Add(this.WithRelationships(snapshot, relationshipFilter));
                }
            }

            batches.Add(
                new TimestampedEntityBatch
                {
                    Timestamp = timestamp,
                    Entities = snapshots,
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var changed = new List<ChangedEntitySnapshot>();
        foreach (var entry in request.EntityIdTimestamps)
        {
            var snapshot = this.TryLoadEntitySnapshot(entry.EntityId);
            if (snapshot is null)
            {
                continue;
            }

            if (this.CompareTimestamp(snapshot.ModifiedTime, entry.Timestamp) > 0)
            {
                changed.Add(
                    new ChangedEntitySnapshot
                    {
                        Entity = snapshot,
                    });
            }
        }

        return Task.FromResult(
            new GetChangedEntitiesResult
            {
                Entities = changed,
            });
    }

    public Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            new GetHistoryResult
            {
                History = request.EntityIds
                    .Select(
                        entityId => new EntityHistoryEntry
                        {
                            EntityId = entityId,
                            UpdateTimes = Array.Empty<Timestamp>(),
                        })
                    .ToArray(),
            });
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new QueryResult
            {
                Batches = request.Timestamps is { Count: > 0 }
                    ? request.Timestamps.Select(timestamp => new TimestampedQueryBatch { Timestamp = timestamp, Entities = Array.Empty<QueryEntitySnapshot>() }).ToArray()
                    : new[] { new TimestampedQueryBatch { Timestamp = null, Entities = Array.Empty<QueryEntitySnapshot>() } },
            });
    }

    public Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.updateLock)
        {
            return Task.FromResult(this.UpdateCore(request));
        }
    }

    private UpdateResult UpdateCore(
        UpdateRequest request)
    {
        var results = new List<EntityUpdateResult>(request.Changes.Count);
        var pendingStates = new Dictionary<EntityId, EntitySnapshot?>();
        var pendingData = new Dictionary<EntityId, JsonElement?>();
        var pendingPreviousData = new Dictionary<EntityId, JsonElement?>();
        var failed = false;

        foreach (var change in request.Changes)
        {
            var entityId = change.EntityId ?? GetEntityId(change.Data);
            if (entityId is null)
            {
                failed = true;
                results.Add(
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

            var current = pendingStates.TryGetValue(entityId.Value, out var pendingSnapshot)
                ? pendingSnapshot
                : this.TryLoadEntitySnapshot(entityId.Value);
            pendingStates[entityId.Value] = current;
            if (!pendingPreviousData.ContainsKey(entityId.Value))
            {
                pendingPreviousData[entityId.Value] = current?.Data?.Clone();
            }

            if (current is not null && current.Data is not null && change.ConcurrencyTag is null)
            {
                if (change.Data is not null && JsonElement.DeepEquals(current.Data.Value, change.Data.Value))
                {
                    results.Add(
                        new EntityUpdateResult
                        {
                            UpdateState = UpdateState.Updated,
                            RequestedEntityId = entityId.Value,
                            ResultingEntityId = entityId.Value,
                            ConcurrencyTag = current.ConcurrencyTag,
                            ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                            CurrentEntity = current,
                            Errors = Array.Empty<UpdateError>(),
                        });
                    continue;
                }

                failed = true;
                results.Add(this.CreateConcurrencyFailure(entityId.Value, current, "Concurrency tag is required."));
                continue;
            }

            if (change.ConcurrencyTag is not null
                && current is not null
                && current.ConcurrencyTag is not null
                && current.ConcurrencyTag.Value != change.ConcurrencyTag.Value)
            {
                failed = true;
                results.Add(this.CreateConcurrencyFailure(entityId.Value, current, "Concurrency tag does not match."));
                continue;
            }

            var nextSequenceNumber = this.nextSequenceNumber + 1;
            var modifiedTime = new Timestamp(DateTimeOffset.UtcNow, nextSequenceNumber.ToString());
            var nextTag = new ConcurrencyTag(nextSequenceNumber.ToString());
            var nextSnapshot = new EntitySnapshot
            {
                EntityId = entityId.Value,
                ConcurrencyTag = nextTag,
                ModifiedTime = modifiedTime,
                Data = change.Data?.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            };

            pendingData[entityId.Value] = change.Data?.Clone();
            pendingStates[entityId.Value] = nextSnapshot;

            results.Add(
                new EntityUpdateResult
                {
                    UpdateState = change.Data is null ? UpdateState.Removed : current is null || current.Data is null ? UpdateState.Added : UpdateState.Updated,
                    RequestedEntityId = entityId.Value,
                    ResultingEntityId = entityId.Value,
                    ConcurrencyTag = nextTag,
                    ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                    CurrentEntity = nextSnapshot,
                    Errors = Array.Empty<UpdateError>(),
                });
        }

        if (failed)
        {
            return new UpdateResult
            {
                EntityResults = results,
            };
        }

        foreach (var entityResult in results)
        {
            var entityId = entityResult.ResultingEntityId;
            if (!pendingData.TryGetValue(entityId, out var data))
            {
                continue;
            }

            var previousData = pendingPreviousData.GetValueOrDefault(entityId);
            this.nextSequenceNumber++;
            this.WriteEntityFile(entityId, data);
            this.WriteEntityMetadata(entityId, entityResult.ConcurrencyTag!.Value, entityResult.CurrentEntity!.ModifiedTime);
            this.UpdateEntityNameIndexes(entityId, previousData, data);
            this.RemoveRelationshipMarkerFiles(entityId);
            this.WriteRelationshipMarkerFiles(entityId, data);
            if (data is null)
            {
                this.deletedEntities[entityId] = entityResult.CurrentEntity!;
            }
            else
            {
                this.deletedEntities.Remove(entityId);
            }
        }

        return new UpdateResult
        {
            EntityResults = results,
        };
    }

    private EntityUpdateResult CreateConcurrencyFailure(
        EntityId entityId,
        EntitySnapshot current,
        string message)
    {
        return new EntityUpdateResult
        {
            UpdateState = UpdateState.Failed,
            RequestedEntityId = entityId,
            ResultingEntityId = entityId,
            ConcurrencyTag = current.ConcurrencyTag,
            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
            CurrentEntity = current,
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

    private EntitySnapshot WithRelationships(
        EntitySnapshot entitySnapshot,
        IReadOnlyCollection<GetRelationshipRequest>? relationshipFilter)
    {
        if (entitySnapshot.Data is null || relationshipFilter is null)
        {
            return entitySnapshot with
            {
                Relationships = Array.Empty<EntitySnapshot>(),
            };
        }

        var relationshipIds = this.ReadRelationshipEntityIds(entitySnapshot.EntityId);
        var relationships = new List<EntitySnapshot>();
        foreach (var relationshipId in relationshipIds)
        {
            var relationship = this.TryLoadEntitySnapshot(relationshipId);
            if (relationship is null || relationship.Data is null)
            {
                continue;
            }

            if (!this.MatchesRelationshipFilter(relationship.Data.Value, relationshipFilter))
            {
                continue;
            }

            relationships.Add(relationship);
        }

        return entitySnapshot with
        {
            Relationships = relationships,
        };
    }

    private bool MatchesRelationshipFilter(
        JsonElement relationshipData,
        IReadOnlyCollection<GetRelationshipRequest> relationshipFilter)
    {
        if (relationshipFilter.Count == 0)
        {
            return true;
        }

        foreach (var filter in relationshipFilter)
        {
            if (!this.MatchesRelationshipTypeNames(relationshipData, filter.RelationshipTypeNames))
            {
                continue;
            }

            if (!this.MatchesRelationshipRoleNames(relationshipData, filter.RelationshipRoleNames))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool MatchesRelationshipTypeNames(
        JsonElement relationshipData,
        RelationshipTypeNameSet? relationshipTypeNames)
    {
        if (relationshipTypeNames is null)
        {
            return true;
        }

        var types = ExtractStringArrayProperty(relationshipData, "entity-types");
        return relationshipTypeNames.Value.Values.All(type => types.Contains(type, StringComparer.Ordinal));
    }

    private bool MatchesRelationshipRoleNames(
        JsonElement relationshipData,
        RoleNameSet? roleNames)
    {
        if (roleNames is null)
        {
            return true;
        }

        if (!relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var roles = participants.EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return roleNames.Value.Values.All(role => roles.Contains(role, StringComparer.Ordinal));
    }

    private IEnumerable<EntityId> FindMatchingEntityIds(
        GetEntityRequest request)
    {
        if (request.EntityId is not null)
        {
            yield return request.EntityId.Value;
            yield break;
        }

        if (request.EntityName is null)
        {
            foreach (var entityId in this.FindEntityIdsByPrefix(new EntityName(Array.Empty<string>())))
            {
                var snapshot = this.TryLoadEntitySnapshot(entityId);
                if (snapshot is null || snapshot.Data is null)
                {
                    continue;
                }

                if (!this.MatchesEntityTypeNames(snapshot.Data.Value, request.EntityTypeNames))
                {
                    continue;
                }

                yield return entityId;
            }

            yield break;
        }

        var candidateEntityIds = request.EnumerateChildren == EnumerateChildrenAction.EnumerateSelf
            ? this.FindEntityIdsByName(request.EntityName.Value)
            : this.FindEntityIdsByPrefix(request.EntityName.Value);

        foreach (var entityId in candidateEntityIds)
        {
            var snapshot = this.TryLoadEntitySnapshot(entityId);
            if (snapshot is null || snapshot.Data is null)
            {
                continue;
            }

            if (!this.MatchesEntityName(snapshot.Data.Value, request.EntityName, request.EnumerateChildren))
            {
                continue;
            }

            if (!this.MatchesEntityTypeNames(snapshot.Data.Value, request.EntityTypeNames))
            {
                continue;
            }

            yield return entityId;
        }
    }

    private bool MatchesEntityName(
        JsonElement entityData,
        EntityName? entityName,
        EnumerateChildrenAction enumerateChildren)
    {
        if (entityName is null)
        {
            return true;
        }

        return ExtractEntityNames(entityData).Any(name => MatchesEntityName(name, entityName.Value, enumerateChildren));
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

    private bool MatchesEntityTypeNames(
        JsonElement entityData,
        EntityTypeNameSet? entityTypeNames)
    {
        if (entityTypeNames is null)
        {
            return true;
        }

        var types = ExtractStringArrayProperty(entityData, "entity-types");
        return entityTypeNames.Value.Values.All(type => types.Contains(type, StringComparer.Ordinal));
    }

    private EntitySnapshot? TryLoadEntitySnapshot(
        EntityId entityId)
    {
        var entityPath = GetEntityPath(this.Path, entityId);
        if (!File.Exists(entityPath))
        {
            return this.deletedEntities.GetValueOrDefault(entityId);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(entityPath));
        var metadata = this.ReadEntityMetadata(entityId);
        return new EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = new ConcurrencyTag(metadata.ConcurrencyTag),
            ModifiedTime = new Timestamp(metadata.ModifiedTimeUtc, metadata.ChangeId),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }

    private IReadOnlyCollection<EntityId> ReadRelationshipEntityIds(
        EntityId participantEntityId)
    {
        var directoryPath = GetEntityDirectory(this.Path, participantEntityId);
        if (!Directory.Exists(directoryPath))
        {
            return Array.Empty<EntityId>();
        }

        var prefix = $"{participantEntityId}_";
        var ids = new List<EntityId>();
        foreach (var markerPath in Directory.EnumerateFiles(directoryPath, $"{prefix}*.rel", SearchOption.TopDirectoryOnly))
        {
            var markerName = System.IO.Path.GetFileNameWithoutExtension(markerPath);
            if (!markerName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relationshipIdText = markerName[prefix.Length..];
            if (Guid.TryParse(relationshipIdText, out var relationshipId))
            {
                ids.Add(new EntityId(relationshipId));
            }
        }

        return ids;
    }

    private void WriteEntityFile(
        EntityId entityId,
        JsonElement? data)
    {
        var directoryPath = GetEntityDirectory(this.Path, entityId);
        Directory.CreateDirectory(directoryPath);
        var filePath = GetEntityPath(this.Path, entityId);
        if (data is null)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return;
        }

        File.WriteAllText(filePath, data.Value.GetRawText());
    }

    private void WriteEntityMetadata(
        EntityId entityId,
        ConcurrencyTag concurrencyTag,
        Timestamp modifiedTime)
    {
        if (modifiedTime.ChangeId.Length > 0
            && long.TryParse(modifiedTime.ChangeId, out var sequence)
            && sequence > this.nextSequenceNumber)
        {
            this.nextSequenceNumber = sequence;
        }

        var metadataPath = GetMetadataPath(this.Path, entityId);
        if (!File.Exists(GetEntityPath(this.Path, entityId)))
        {
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }

            return;
        }

        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(
                new EntityMetadata
                {
                    ConcurrencyTag = concurrencyTag.Value,
                    ModifiedTimeUtc = modifiedTime.DateTime,
                    ChangeId = modifiedTime.ChangeId,
                }));
    }

    private EntityMetadata ReadEntityMetadata(
        EntityId entityId)
    {
        var metadataPath = GetMetadataPath(this.Path, entityId);
        if (File.Exists(metadataPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            if (root.TryGetProperty("concurrencyTag", out var concurrencyTagElement)
                && concurrencyTagElement.ValueKind == JsonValueKind.String
                && root.TryGetProperty("modifiedTimeUtc", out var modifiedTimeElement)
                && modifiedTimeElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(modifiedTimeElement.GetString(), out var modifiedTime)
                && root.TryGetProperty("changeId", out var changeIdElement)
                && changeIdElement.ValueKind == JsonValueKind.String)
            {
                return new EntityMetadata
                {
                    ConcurrencyTag = concurrencyTagElement.GetString()!,
                    ModifiedTimeUtc = modifiedTime,
                    ChangeId = changeIdElement.GetString()!,
                };
            }
        }

        var filePath = GetEntityPath(this.Path, entityId);
        var fileTime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.UnixEpoch;
        var fallback = this.nextSequenceNumber.ToString();
        return new EntityMetadata
        {
            ConcurrencyTag = fallback,
            ModifiedTimeUtc = fileTime,
            ChangeId = fallback,
        };
    }

    private long LoadNextSequenceNumber()
    {
        var maxSequence = 0L;
        foreach (var metadataPath in Directory.EnumerateFiles(this.Path, "*.meta", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                if (document.RootElement.TryGetProperty("changeId", out var changeIdElement)
                    && changeIdElement.ValueKind == JsonValueKind.String
                    && long.TryParse(changeIdElement.GetString(), out var sequence)
                    && sequence > maxSequence)
                {
                    maxSequence = sequence;
                }
            }
            catch (JsonException)
            {
            }
        }

        return maxSequence;
    }

    private void WriteRelationshipMarkerFiles(
        EntityId relationshipEntityId,
        JsonElement? relationshipData)
    {
        var participantEntityIds = ExtractRelationshipParticipantEntityIds(relationshipData);
        if (participantEntityIds.Count == 0)
        {
            return;
        }

        foreach (var participantEntityId in participantEntityIds)
        {
            var directoryPath = GetEntityDirectory(this.Path, participantEntityId);
            Directory.CreateDirectory(directoryPath);
            var markerPath = System.IO.Path.Combine(
                directoryPath,
                $"{participantEntityId}_{relationshipEntityId}.rel");
            File.WriteAllText(markerPath, string.Empty);
        }
    }

    private void RemoveRelationshipMarkerFiles(
        EntityId relationshipEntityId)
    {
        var markerSuffix = $"_{relationshipEntityId}.rel";
        var entitiesRootPath = GetEntitiesRootPath(this.Path);
        if (!Directory.Exists(entitiesRootPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(entitiesRootPath, $"*{markerSuffix}", SearchOption.AllDirectories))
        {
            File.Delete(filePath);
        }
    }

    private void UpdateEntityNameIndexes(
        EntityId entityId,
        JsonElement? previousData,
        JsonElement? nextData)
    {
        var previousNames = ExtractEntityNames(previousData);
        var nextNames = ExtractEntityNames(nextData);

        var previousNameHashes = previousNames
            .Select(ComputeEntityNameHash)
            .ToHashSet(StringComparer.Ordinal);
        var nextNameHashes = nextNames
            .Select(ComputeEntityNameHash)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var removedNameHash in previousNameHashes.Except(nextNameHashes))
        {
            DeleteIndexFile(GetEntityNameIndexFilePath(this.Path, removedNameHash, entityId));
        }

        foreach (var addedNameHash in nextNameHashes.Except(previousNameHashes))
        {
            CreateZeroLengthFile(GetEntityNameIndexFilePath(this.Path, addedNameHash, entityId));
        }

        var previousPrefixHashes = GetEntityNamePrefixHashes(previousNames);
        var nextPrefixHashes = GetEntityNamePrefixHashes(nextNames);
        foreach (var removedPrefixHash in previousPrefixHashes.Except(nextPrefixHashes))
        {
            DeleteIndexFile(GetEntityNamePrefixIndexFilePath(this.Path, removedPrefixHash, entityId));
        }

        foreach (var addedPrefixHash in nextPrefixHashes.Except(previousPrefixHashes))
        {
            CreateZeroLengthFile(GetEntityNamePrefixIndexFilePath(this.Path, addedPrefixHash, entityId));
        }
    }

    private IReadOnlyCollection<EntityId> FindEntityIdsByName(
        EntityName entityName)
    {
        var hash = ComputeEntityNameHash(entityName);
        var indexDirectoryPath = GetEntityNameIndexDirectoryPath(this.Path, hash);
        if (!Directory.Exists(indexDirectoryPath))
        {
            return Array.Empty<EntityId>();
        }

        var expectedFileNamePrefix = $"{hash}_";
        var matchingEntityIds = new List<EntityId>();
        foreach (var filePath in Directory.EnumerateFiles(indexDirectoryPath, $"{expectedFileNamePrefix}*", SearchOption.TopDirectoryOnly))
        {
            var fileName = System.IO.Path.GetFileName(filePath);
            if (!fileName.StartsWith(expectedFileNamePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var entityIdText = fileName[expectedFileNamePrefix.Length..];
            if (!Guid.TryParse(entityIdText, out var entityId))
            {
                continue;
            }

            matchingEntityIds.Add(new EntityId(entityId));
        }

        return matchingEntityIds;
    }

    private IReadOnlyCollection<EntityId> FindEntityIdsByPrefix(
        EntityName prefix)
    {
        var prefixHash = ComputeEntityNameHash(prefix);
        var prefixDirectoryPath = GetEntityNamePrefixIndexDirectoryPath(this.Path, prefixHash);
        if (!Directory.Exists(prefixDirectoryPath))
        {
            return Array.Empty<EntityId>();
        }

        var matchingEntityIds = new HashSet<EntityId>();
        foreach (var filePath in Directory.EnumerateFiles(prefixDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var entityIdText = System.IO.Path.GetFileName(filePath);
            if (!Guid.TryParse(entityIdText, out var entityId))
            {
                continue;
            }

            matchingEntityIds.Add(new EntityId(entityId));
        }

        return matchingEntityIds;
    }

    private IEnumerable<EntityId> EnumerateEntityIdsFromFiles()
    {
        var entitiesRootPath = GetEntitiesRootPath(this.Path);
        if (!Directory.Exists(entitiesRootPath))
        {
            yield break;
        }

        foreach (var entityFilePath in Directory.EnumerateFiles(entitiesRootPath, "*.json", SearchOption.AllDirectories))
        {
            if (TryGetEntityIdFromDalPath(entitiesRootPath, entityFilePath, out var entityId))
            {
                yield return entityId;
            }
        }
    }

    private int CompareTimestamp(
        Timestamp left,
        Timestamp right)
    {
        var dateTimeComparison = left.DateTime.CompareTo(right.DateTime);
        if (dateTimeComparison != 0)
        {
            return dateTimeComparison;
        }

        return string.CompareOrdinal(left.ChangeId, right.ChangeId);
    }

    private static IReadOnlyCollection<string> ExtractStringArrayProperty(
        JsonElement entityData,
        string propertyName)
    {
        if (!entityData.TryGetProperty(propertyName, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var element in arrayElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static EntityId? GetEntityId(
        JsonElement? data)
    {
        if (data is null
            || data.Value.ValueKind != JsonValueKind.Object
            || !data.Value.TryGetProperty("entity-id", out var entityIdElement)
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
            if (TryGetEntityName(nameElement, out var entityName))
            {
                names.Add(entityName);
            }
        }

        return names;
    }

    private static bool TryGetEntityName(
        JsonElement nameElement,
        out EntityName entityName)
    {
        entityName = default;
        var parsedEntityName = nameElement.TryReadEntityName();
        if (parsedEntityName is null)
        {
            return false;
        }

        entityName = parsedEntityName.Value;
        return true;
    }

    private static HashSet<string> GetEntityNamePrefixHashes(
        IReadOnlyCollection<EntityName> names)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            for (var index = 0; index <= name.Components.Length; index++)
            {
                hashes.Add(ComputeEntityNameHash(new EntityName(name.Components[..index])));
            }
        }

        return hashes;
    }

    public static string ComputeEntityNameHash(
        EntityName entityName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(entityName.Components.Length);
        foreach (var component in entityName.Components)
        {
            var componentBytes = Encoding.UTF8.GetBytes(component);
            writer.Write(componentBytes.Length);
            writer.Write(componentBytes);
        }

        writer.Flush();
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream.ToArray()))[..24].ToLowerInvariant();
    }

    private static IReadOnlyCollection<EntityId> ExtractRelationshipParticipantEntityIds(
        JsonElement? relationshipData)
    {
        if (relationshipData is null
            || relationshipData.Value.ValueKind != JsonValueKind.Object
            || !TryGetRelationshipParticipantIds(relationshipData.Value, out var relatedEntityIds))
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

    public static string GetEntityDirectory(
        string rootPath,
        EntityId entityId)
    {
        var bytes = entityId.Value.ToByteArray();
        return System.IO.Path.Combine(
            GetEntitiesRootPath(rootPath),
            bytes[0].ToString("x2"),
            bytes[1].ToString("x2"),
            bytes[2].ToString("x2"));
    }

    private static string GetEntityPath(
        string rootPath,
        EntityId entityId)
    {
        return System.IO.Path.Combine(GetEntityDirectory(rootPath, entityId), $"{entityId}.json");
    }

    private static string GetMetadataPath(
        string rootPath,
        EntityId entityId)
    {
        return System.IO.Path.Combine(GetEntityDirectory(rootPath, entityId), $"{entityId}.meta");
    }

    private static string GetEntitiesRootPath(
        string rootPath)
    {
        return System.IO.Path.Combine(rootPath, EntitiesDirectoryName);
    }

    private static string GetEntityNameIndexRootPath(
        string rootPath)
    {
        return System.IO.Path.Combine(rootPath, EntityNamesIndexDirectoryName);
    }

    private static string GetEntityNamePrefixesIndexRootPath(
        string rootPath)
    {
        return System.IO.Path.Combine(rootPath, EntityNamePrefixesIndexDirectoryName);
    }

    private static string GetEntityNameIndexDirectoryPath(
        string rootPath,
        string nameHash)
    {
        return GetShardedDirectoryPath(GetEntityNameIndexRootPath(rootPath), nameHash);
    }

    private static string GetEntityNameIndexFilePath(
        string rootPath,
        string nameHash,
        EntityId entityId)
    {
        var entityIdText = entityId.Value.ToString("N");
        return System.IO.Path.Combine(GetEntityNameIndexDirectoryPath(rootPath, nameHash), $"{nameHash}_{entityIdText}");
    }

    private static string GetEntityNamePrefixIndexFilePath(
        string rootPath,
        string prefixHash,
        EntityId entityId)
    {
        var entityIdText = entityId.Value.ToString("N");
        var prefixShardPath = GetEntityNamePrefixIndexDirectoryPath(rootPath, prefixHash);
        return System.IO.Path.Combine(
            GetShardedDirectoryPath(prefixShardPath, entityIdText),
            entityIdText);
    }

    private static string GetEntityNamePrefixIndexDirectoryPath(
        string rootPath,
        string prefixHash)
    {
        return System.IO.Path.Combine(
            GetShardedDirectoryPath(GetEntityNamePrefixesIndexRootPath(rootPath), prefixHash),
            prefixHash);
    }

    private static string GetShardedDirectoryPath(
        string rootPath,
        string shardKey)
    {
        if (shardKey.Length < 6)
        {
            throw new ArgumentException("Shard key must be at least 6 hexadecimal characters.", nameof(shardKey));
        }

        return System.IO.Path.Combine(rootPath, shardKey[..2], shardKey.Substring(2, 2), shardKey.Substring(4, 2));
    }

    private static void CreateZeroLengthFile(
        string filePath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        using var file = File.Create(filePath);
    }

    private static void DeleteIndexFile(
        string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        File.Delete(filePath);
    }

    private static bool TryGetEntityIdFromDalPath(
        string rootPath,
        string entityFilePath,
        out EntityId entityId)
    {
        entityId = default;
        var relativePath = System.IO.Path.GetRelativePath(rootPath, entityFilePath).Replace('\\', '/');
        var pathParts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length != 4
            || !IsTwoHexCharacters(pathParts[0])
            || !IsTwoHexCharacters(pathParts[1])
            || !IsTwoHexCharacters(pathParts[2]))
        {
            return false;
        }

        var fileName = pathParts[3];
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var entityIdText = fileName[..^5];
        if (!Guid.TryParse(entityIdText, out var parsedEntityId))
        {
            return false;
        }

        entityId = new EntityId(parsedEntityId);
        return true;
    }

    private static bool IsTwoHexCharacters(
        string value)
    {
        return value.Length == 2
            && Uri.IsHexDigit(value[0])
            && Uri.IsHexDigit(value[1]);
    }

    private sealed record EntityMetadata
    {
        [JsonPropertyName("concurrencyTag")]
        public required string ConcurrencyTag { get; init; }

        [JsonPropertyName("modifiedTimeUtc")]
        public required DateTimeOffset ModifiedTimeUtc { get; init; }

        [JsonPropertyName("changeId")]
        public required string ChangeId { get; init; }
    }
}
