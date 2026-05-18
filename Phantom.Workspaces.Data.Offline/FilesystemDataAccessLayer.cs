using System.Text.Json;
using System.Text.Json.Serialization;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Offline;

/// <summary>
/// Filesystem-backed DAL implementation.
/// </summary>
public sealed class FilesystemDataAccessLayer : IDataAccessLayer
{
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
        var knownEntityIds = this.GetKnownEntityIds();

        foreach (var timestamp in timestamps)
        {
            var snapshots = new List<EntitySnapshot>();
            var includedEntityIds = new HashSet<EntityId>();
            foreach (var requestedEntity in request.Entities)
            {
                foreach (var entityId in this.FindMatchingEntityIds(requestedEntity, knownEntityIds))
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

            if (current is not null && current.Data is not null && change.ConcurrencyTag is null)
            {
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
            var data = pendingData[entityId];
            this.nextSequenceNumber++;
            this.WriteEntityFile(entityId, data);
            this.WriteEntityMetadata(entityId, entityResult.ConcurrencyTag!.Value, entityResult.CurrentEntity!.ModifiedTime);
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

    private HashSet<EntityId> GetKnownEntityIds()
    {
        var ids = this.EnumerateEntityIdsFromFiles().ToHashSet();
        foreach (var deletedEntityId in this.deletedEntities.Keys)
        {
            ids.Add(deletedEntityId);
        }

        return ids;
    }

    private IEnumerable<EntityId> FindMatchingEntityIds(
        GetEntityRequest request,
        HashSet<EntityId> knownEntityIds)
    {
        if (request.EntityId is not null)
        {
            if (knownEntityIds.Contains(request.EntityId.Value))
            {
                yield return request.EntityId.Value;
            }

            yield break;
        }

        foreach (var entityId in knownEntityIds)
        {
            var snapshot = this.TryLoadEntitySnapshot(entityId);
            if (snapshot is null || snapshot.Data is null)
            {
                continue;
            }

            if (!this.MatchesEntityName(snapshot.Data.Value, request.EntityName))
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
        EntityName? entityName)
    {
        if (entityName is null)
        {
            return true;
        }

        var names = entityData.ExtractStringArray("names");
        foreach (var name in names)
        {
            var components = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.SequenceEqual(entityName.Value.Components, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
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
        foreach (var filePath in Directory.EnumerateFiles(this.Path, $"*{markerSuffix}", SearchOption.AllDirectories))
        {
            File.Delete(filePath);
        }
    }

    private IEnumerable<EntityId> EnumerateEntityIdsFromFiles()
    {
        foreach (var entityFilePath in Directory.EnumerateFiles(this.Path, "*.json", SearchOption.AllDirectories))
        {
            if (TryGetEntityIdFromDalPath(this.Path, entityFilePath, out var entityId))
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
            rootPath,
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
