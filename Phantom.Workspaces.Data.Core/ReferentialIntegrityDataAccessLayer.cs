using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs referential integrity validation on data being updated on an underlying IDataAccessLayer.
/// This layer also manages synthetic "reference" relationship entities for object-property references.
/// </summary>
public class ReferentialIntegrityDataAccessLayer : SchemaValidatingDataAccessLayer
{
    private const string ReferenceRelationshipType = "reference";
    private const string RelationshipType = "relationship";
    private const string FolderType = "folder";
    private const string EntityTypeType = "entity-type";
    private const string JsonSchemaType = "json-schema";
    private const string FolderSchema = "https://schemas.workspaces.phantom.to/workspaces/data/core/folder.json";

    public ReferentialIntegrityDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
        : base(underlyingDataAccessLayer)
    {
    }

    public override async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rewriteState = await this.BuildRewrittenChangesAsync(request, cancellationToken).ConfigureAwait(false);
        if (rewriteState.Failures.Count > 0)
        {
            return new UpdateResult
            {
                EntityResults = rewriteState.Failures,
            };
        }

        return await base.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = request.UpdateMetadata,
                Changes = rewriteState.Changes,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RewriteState> BuildRewrittenChangesAsync(
        UpdateRequest request,
        CancellationToken cancellationToken)
    {
        var unresolvedChanges = new List<OrderedChange>();
        var orderedChangesByEntityId = new Dictionary<EntityId, OrderedChange>();
        var order = 0;

        foreach (var change in request.Changes)
        {
            var resolvedEntityId = this.ResolveEntityId(change);
            if (resolvedEntityId is null)
            {
                unresolvedChanges.Add(
                    new OrderedChange
                    {
                        Order = order++,
                        Change = change,
                    });
                continue;
            }

            if (!orderedChangesByEntityId.TryGetValue(resolvedEntityId.Value, out var existingOrderedChange))
            {
                orderedChangesByEntityId[resolvedEntityId.Value] = new OrderedChange
                {
                    Order = order++,
                    Change = change with { EntityId = resolvedEntityId.Value },
                };
                continue;
            }

            orderedChangesByEntityId[resolvedEntityId.Value] = new OrderedChange
            {
                Order = existingOrderedChange.Order,
                Change = change with { EntityId = resolvedEntityId.Value },
            };
        }

        var changesByEntityId = orderedChangesByEntityId.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Change);

        var currentSnapshotsById = await this.GetCurrentSnapshotsByIdAsync(
            changesByEntityId.Keys,
            includeRelationships: true,
            cancellationToken).ConfigureAwait(false);

        this.ApplyManagedReferenceRelationshipChanges(changesByEntityId, currentSnapshotsById, orderedChangesByEntityId, ref order);
        this.ApplyRelationshipDeleteCascade(changesByEntityId, currentSnapshotsById, orderedChangesByEntityId, ref order);
        var folderDeleteFailures = await this.ValidateFolderDeletesAsync(changesByEntityId, currentSnapshotsById, cancellationToken).ConfigureAwait(false);
        if (folderDeleteFailures.Count > 0)
        {
            return new RewriteState
            {
                Changes = Array.Empty<EntityChange>(),
                Failures = folderDeleteFailures,
            };
        }

        order = await this.ApplyFolderPrefixEntityChangesAsync(changesByEntityId, orderedChangesByEntityId, order, cancellationToken).ConfigureAwait(false);
        await this.ApplyDuplicateRelationshipCoalescingAsync(changesByEntityId, currentSnapshotsById, orderedChangesByEntityId, cancellationToken).ConfigureAwait(false);
        var requestSchemasByName = this.GetSchemasFromRequest(
            new UpdateRequest
            {
                UpdateMetadata = request.UpdateMetadata,
                Changes = changesByEntityId.Values.ToArray(),
            });

        var validationFailures = await this.ValidateReferencesAsync(
            changesByEntityId,
            requestSchemasByName,
            currentSnapshotsById,
            cancellationToken).ConfigureAwait(false);
        if (validationFailures.Count > 0)
        {
            return new RewriteState
            {
                Changes = Array.Empty<EntityChange>(),
                Failures = validationFailures,
            };
        }

        var rewrittenChanges = unresolvedChanges
            .Concat(orderedChangesByEntityId.Values)
            .OrderBy(static orderedChange => orderedChange.Order)
            .Select(static orderedChange => orderedChange.Change)
            .ToArray();
        return new RewriteState
        {
            Changes = rewrittenChanges,
            Failures = Array.Empty<EntityUpdateResult>(),
        };
    }

    private async Task ApplyDuplicateRelationshipCoalescingAsync(
        IDictionary<EntityId, EntityChange> changesByEntityId,
        IDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        IDictionary<EntityId, OrderedChange> orderedChangesByEntityId,
        CancellationToken cancellationToken)
    {
        var relationshipChanges = changesByEntityId
            .Where(static pair => pair.Value.Data is not null)
            .Where(pair => this.IsRelationshipEntity(pair.Value.Data!.Value))
            .Select(static pair => (EntityId: pair.Key, Change: pair.Value))
            .ToArray();
        if (relationshipChanges.Length == 0)
        {
            return;
        }

        var seenByKey = new Dictionary<string, EntityId>(StringComparer.Ordinal);
        foreach (var relationshipChange in relationshipChanges)
        {
            var participants = this.GetRelationshipParticipantEntityIds(relationshipChange.Change.Data!.Value);
            foreach (var participant in participants)
            {
                var participantSnapshot = await this.EnsureEntitySnapshotWithRelationshipsAsync(participant, currentSnapshotsById, cancellationToken).ConfigureAwait(false);
                if (participantSnapshot is null)
                {
                    continue;
                }

                foreach (var relationshipSnapshot in participantSnapshot.Relationships)
                {
                    if (relationshipSnapshot.Data is null)
                    {
                        continue;
                    }

                    var existingRelationshipKey = this.GetRelationshipKey(relationshipSnapshot.Data.Value);
                    if (existingRelationshipKey is null)
                    {
                        continue;
                    }

                    seenByKey.TryAdd(existingRelationshipKey, relationshipSnapshot.EntityId);
                }
            }
        }

        foreach (var relationshipChange in relationshipChanges)
        {
            var relationshipData = relationshipChange.Change.Data!.Value;
            var relationshipKey = this.GetRelationshipKey(relationshipData);
            if (relationshipKey is null)
            {
                continue;
            }

            if (!seenByKey.TryGetValue(relationshipKey, out var existingRelationshipId))
            {
                seenByKey[relationshipKey] = relationshipChange.EntityId;
                continue;
            }

            if (existingRelationshipId == relationshipChange.EntityId)
            {
                continue;
            }

            if (changesByEntityId.TryGetValue(existingRelationshipId, out var existingChange)
                && existingChange.Data is null)
            {
                seenByKey[relationshipKey] = relationshipChange.EntityId;
                continue;
            }

            changesByEntityId.Remove(relationshipChange.EntityId);
            orderedChangesByEntityId.Remove(relationshipChange.EntityId);
        }
    }

    private async Task<EntitySnapshot?> EnsureEntitySnapshotWithRelationshipsAsync(
        EntityId entityId,
        IDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        CancellationToken cancellationToken)
    {
        if (currentSnapshotsById.TryGetValue(entityId, out var existingSnapshot))
        {
            return existingSnapshot;
        }

        var fetchedSnapshots = await this.GetCurrentSnapshotsByIdAsync(
            new[] { entityId },
            includeRelationships: true,
            cancellationToken).ConfigureAwait(false);
        if (!fetchedSnapshots.TryGetValue(entityId, out var fetchedSnapshot))
        {
            return null;
        }

        currentSnapshotsById[entityId] = fetchedSnapshot;
        return fetchedSnapshot;
    }

    private void ApplyManagedReferenceRelationshipChanges(
        IDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        IDictionary<EntityId, OrderedChange> orderedChangesByEntityId,
        ref int nextOrder)
    {
        foreach (var entityId in changesByEntityId.Keys.ToArray())
        {
            var change = changesByEntityId[entityId];
            if (change.Data is null
                || this.IsReferenceRelationshipEntity(change.Data.Value)
                || this.IsRelationshipEntity(change.Data.Value))
            {
                continue;
            }

            var previousData = currentSnapshotsById.TryGetValue(entityId, out var currentSnapshot)
                ? currentSnapshot.Data
                : null;
            var previousReferenceCounts = this.GetManagedReferenceCounts(previousData);
            var newReferenceCounts = this.GetManagedReferenceCounts(change.Data.Value);
            var allTargets = previousReferenceCounts.Keys
                .Concat(newReferenceCounts.Keys)
                .Distinct()
                .ToArray();

            foreach (var targetEntityId in allTargets)
            {
                var hadReference = previousReferenceCounts.TryGetValue(targetEntityId, out var previousCount) && previousCount > 0;
                var hasReference = newReferenceCounts.TryGetValue(targetEntityId, out var newCount) && newCount > 0;
                var relationshipEntityId = this.GetManagedReferenceRelationshipEntityId(entityId, targetEntityId);

                if (hasReference)
                {
                    if (changesByEntityId.TryGetValue(relationshipEntityId, out var existingRelationshipChange)
                        && existingRelationshipChange.Data is null)
                    {
                        // Explicit deletion in request wins over managed creation/update.
                        continue;
                    }

                    currentSnapshotsById.TryGetValue(relationshipEntityId, out var currentRelationship);
                    currentRelationship ??= currentSnapshot?.Relationships
                        .FirstOrDefault(relationship => relationship.EntityId == relationshipEntityId);
                    this.UpsertChange(
                        changesByEntityId,
                        orderedChangesByEntityId,
                        relationshipEntityId,
                        new EntityChange
                        {
                            EntityId = relationshipEntityId,
                            ConcurrencyTag = currentRelationship?.ConcurrencyTag,
                            Data = this.CreateManagedReferenceRelationshipEntity(
                                relationshipEntityId,
                                entityId,
                                targetEntityId),
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                        ref nextOrder);
                    continue;
                }

                if (!hadReference)
                {
                    continue;
                }

                if (changesByEntityId.TryGetValue(relationshipEntityId, out var currentManagedChange)
                    && currentManagedChange.Data is null)
                {
                    continue;
                }

                currentSnapshotsById.TryGetValue(relationshipEntityId, out var currentRelationshipToDelete);
                currentRelationshipToDelete ??= currentSnapshot?.Relationships
                    .FirstOrDefault(relationship => relationship.EntityId == relationshipEntityId);
                this.UpsertChange(
                    changesByEntityId,
                    orderedChangesByEntityId,
                    relationshipEntityId,
                    new EntityChange
                    {
                        EntityId = relationshipEntityId,
                        ConcurrencyTag = currentRelationshipToDelete?.ConcurrencyTag,
                        Data = null,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                    ref nextOrder);
            }
        }
    }

    private void ApplyRelationshipDeleteCascade(
        IDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        IDictionary<EntityId, OrderedChange> orderedChangesByEntityId,
        ref int nextOrder)
    {
        var deletedEntityIds = changesByEntityId
            .Where(static pair => pair.Value.Data is null)
            .Select(static pair => pair.Key)
            .ToArray();
        if (deletedEntityIds.Length == 0)
        {
            return;
        }

        foreach (var deletedEntityId in deletedEntityIds)
        {
            if (currentSnapshotsById.TryGetValue(deletedEntityId, out var currentEntity))
            {
                foreach (var relationship in currentEntity.Relationships)
                {
                    this.UpsertChange(
                        changesByEntityId,
                        orderedChangesByEntityId,
                        relationship.EntityId,
                        new EntityChange
                        {
                            EntityId = relationship.EntityId,
                            ConcurrencyTag = relationship.ConcurrencyTag,
                            Data = null,
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                        ref nextOrder);
                }
            }

            foreach (var pair in changesByEntityId.ToArray())
            {
                if (pair.Value.Data is null
                    || !this.IsRelationshipEntity(pair.Value.Data.Value))
                {
                    continue;
                }

                var participants = this.GetRelationshipParticipantEntityIds(pair.Value.Data.Value);
                if (!participants.Contains(deletedEntityId))
                {
                    continue;
                }

                currentSnapshotsById.TryGetValue(pair.Key, out var currentRelationship);
                this.UpsertChange(
                    changesByEntityId,
                    orderedChangesByEntityId,
                    pair.Key,
                    new EntityChange
                    {
                        EntityId = pair.Key,
                        ConcurrencyTag = currentRelationship?.ConcurrencyTag ?? pair.Value.ConcurrencyTag,
                        Data = null,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                    ref nextOrder);
            }
        }
    }

    private async Task<int> ApplyFolderPrefixEntityChangesAsync(
        IDictionary<EntityId, EntityChange> changesByEntityId,
        IDictionary<EntityId, OrderedChange> orderedChangesByEntityId,
        int nextOrder,
        CancellationToken cancellationToken)
    {
        var latestSnapshotsById = await this.GetLatestSnapshotsByIdAsync(cancellationToken).ConfigureAwait(false);
        var projectedEntitiesById = latestSnapshotsById.ToDictionary(
            static pair => pair.Key,
            static pair => (Data: pair.Value.Data, pair.Value.ConcurrencyTag));

        foreach (var pair in changesByEntityId)
        {
            projectedEntitiesById[pair.Key] = (pair.Value.Data, pair.Value.ConcurrencyTag);
        }

        var requiredFolderNames = new Dictionary<string, EntityName>(StringComparer.Ordinal);
        var existingFolderByName = new Dictionary<string, (EntityId EntityId, ConcurrencyTag? ConcurrencyTag)>(StringComparer.Ordinal);
        var hasNamedEntities = false;
        foreach (var pair in projectedEntitiesById)
        {
            if (pair.Value.Data is not { ValueKind: JsonValueKind.Object } entityData)
            {
                continue;
            }

            var names = this.GetEntityNameValues(entityData);
            if (names.Count > 0)
            {
                hasNamedEntities = true;
            }

            if (this.IsFolderEntity(entityData))
            {
                foreach (var name in names)
                {
                    var nameKey = SerializeEntityName(name);
                    existingFolderByName.TryAdd(nameKey, (pair.Key, pair.Value.ConcurrencyTag));
                }

                continue;
            }

            foreach (var name in names)
            {
                for (var componentCount = 1; componentCount < name.Components.Length; componentCount++)
                {
                    var prefixName = new EntityName(name.Components[..componentCount]);
                    requiredFolderNames[SerializeEntityName(prefixName)] = prefixName;
                }
            }
        }

        if (hasNamedEntities)
        {
            var rootFolderName = EntityName.Root;
            requiredFolderNames[SerializeEntityName(rootFolderName)] = rootFolderName;
        }

        foreach (var requiredFolder in requiredFolderNames)
        {
            if (existingFolderByName.ContainsKey(requiredFolder.Key))
            {
                continue;
            }

            var folderEntityId = GetFolderEntityId(requiredFolder.Value);
            var existingConcurrencyTag = changesByEntityId.TryGetValue(folderEntityId, out var existingChange)
                ? existingChange.ConcurrencyTag
                : latestSnapshotsById.TryGetValue(folderEntityId, out var existingSnapshot) ? existingSnapshot.ConcurrencyTag : null;
            this.UpsertChange(
                changesByEntityId,
                orderedChangesByEntityId,
                folderEntityId,
                new EntityChange
                {
                    EntityId = folderEntityId,
                    ConcurrencyTag = existingConcurrencyTag,
                    Data = CreateFolderEntityData(folderEntityId, requiredFolder.Value),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
                ref nextOrder);
        }

        return nextOrder;
    }

    private async Task<IReadOnlyCollection<EntityUpdateResult>> ValidateReferencesAsync(
        IReadOnlyDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        CancellationToken cancellationToken)
    {
        var referencedEntityIds = new HashSet<EntityId>();
        var referencedEntityNames = new HashSet<string>(StringComparer.Ordinal);
        var referencesBySource = new Dictionary<EntityId, List<ReferenceConstraint>>();

        foreach (var pair in changesByEntityId)
        {
            if (pair.Value.Data is null)
            {
                continue;
            }

            var sourceEntityId = pair.Key;
            var sourceData = pair.Value.Data.Value;
            if (this.IsSchemaEntity(sourceData))
            {
                continue;
            }

            var references = await this.ExtractReferencesAsync(
                sourceData,
                requestSchemaEntitiesByName,
                cancellationToken).ConfigureAwait(false);
            referencesBySource[sourceEntityId] = references;
            foreach (var reference in references)
            {
                if (reference.TargetEntityId is { } targetEntityId)
                {
                    referencedEntityIds.Add(targetEntityId);
                }

                if (!string.IsNullOrWhiteSpace(reference.TargetEntityName))
                {
                    referencedEntityNames.Add(reference.TargetEntityName);
                }
            }
        }

        var remainingReferencedIds = referencedEntityIds
            .Where(entityId => !changesByEntityId.ContainsKey(entityId))
            .ToArray();
        var currentReferencedSnapshotsById = await this.GetCurrentSnapshotsByIdAsync(
            remainingReferencedIds,
            includeRelationships: false,
            cancellationToken).ConfigureAwait(false);
        var requestEntitiesByName = this.GetEntitiesByName(changesByEntityId.Values
            .Where(static change => change.Data is { ValueKind: JsonValueKind.Object })
            .Select(static change => change.Data!.Value));
        var currentChangedEntitiesByName = this.GetEntitiesByName(currentSnapshotsById.Values
            .Where(static snapshot => snapshot.Data is { ValueKind: JsonValueKind.Object })
            .Select(static snapshot => snapshot.Data!.Value));
        var unresolvedReferencedNames = referencedEntityNames
            .Where(name => !requestEntitiesByName.ContainsKey(name) && !currentChangedEntitiesByName.ContainsKey(name))
            .ToArray();
        var currentReferencedEntitiesByName = await this.GetCurrentEntitiesByNameAsync(unresolvedReferencedNames, cancellationToken).ConfigureAwait(false);

        var failures = new List<EntityUpdateResult>();
        foreach (var pair in referencesBySource)
        {
            var sourceEntityId = pair.Key;
            var sourceChange = changesByEntityId[sourceEntityId];
            var errors = new List<UpdateError>();

            foreach (var reference in pair.Value)
            {
                var targetCandidates = this.ResolveEffectiveEntities(
                    reference,
                    changesByEntityId,
                    currentReferencedSnapshotsById,
                    requestEntitiesByName,
                    currentChangedEntitiesByName,
                    currentReferencedEntitiesByName);
                if (targetCandidates.Count == 0)
                {
                    errors.Add(
                        new UpdateError
                        {
                            Message = $"Referenced entity '{this.GetReferenceDisplay(reference)}' does not exist.",
                            RelatedEntityId = reference.TargetEntityId,
                        });
                    continue;
                }

                if (reference.RequiredTypes is null || reference.RequiredTypes.Count == 0)
                {
                    continue;
                }

                var hasMatchingType = targetCandidates.Any(
                    candidate =>
                    {
                        var targetTypes = this.GetEntityTypeNames(candidate);
                        return reference.RequiredTypes.Any(targetTypes.Contains);
                    });
                if (!hasMatchingType)
                {
                    errors.Add(
                        new UpdateError
                        {
                            Message = $"Referenced entity '{this.GetReferenceDisplay(reference)}' does not match required types: {string.Join(", ", reference.RequiredTypes)}.",
                            RelatedEntityId = reference.TargetEntityId,
                        });
                }
            }

            if (errors.Count == 0)
            {
                continue;
            }

            currentSnapshotsById.TryGetValue(sourceEntityId, out var currentEntity);
            failures.Add(
                new EntityUpdateResult
                {
                    UpdateState = UpdateState.Failed,
                    RequestedEntityId = sourceEntityId,
                    ResultingEntityId = sourceEntityId,
                    ConcurrencyTag = currentEntity?.ConcurrencyTag,
                    ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                    CurrentEntity = currentEntity,
                    Errors = errors,
                });
        }

        return failures;
    }

    private async Task<IReadOnlyCollection<EntityUpdateResult>> ValidateFolderDeletesAsync(
        IReadOnlyDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        CancellationToken cancellationToken)
    {
        var folderDeleteEntityIds = changesByEntityId
            .Where(static pair => pair.Value.Data is null)
            .Select(static pair => pair.Key)
            .Where(
                entityId => currentSnapshotsById.TryGetValue(entityId, out var currentSnapshot)
                    && currentSnapshot.Data is { ValueKind: JsonValueKind.Object } currentData
                    && this.IsFolderEntity(currentData))
            .ToArray();
        if (folderDeleteEntityIds.Length == 0)
        {
            return Array.Empty<EntityUpdateResult>();
        }

        var latestSnapshotsById = await this.GetLatestSnapshotsByIdAsync(cancellationToken).ConfigureAwait(false);
        var projectedEntitiesById = latestSnapshotsById.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Data);
        foreach (var pair in changesByEntityId)
        {
            projectedEntitiesById[pair.Key] = pair.Value.Data;
        }

        var projectedNames = projectedEntitiesById.Values
            .Where(static data => data is { ValueKind: JsonValueKind.Object })
            .SelectMany(data => this.GetEntityNameValues(data!.Value))
            .ToArray();

        var failures = new List<EntityUpdateResult>();
        foreach (var folderDeleteEntityId in folderDeleteEntityIds)
        {
            var currentSnapshot = currentSnapshotsById[folderDeleteEntityId];
            if (currentSnapshot.Data is not { ValueKind: JsonValueKind.Object } folderData)
            {
                continue;
            }

            var folderNames = this.GetEntityNameValues(folderData);
            var hasRemainingDescendants = folderNames.Any(
                folderName => projectedNames.Any(name => IsDescendantName(name, folderName)));
            if (!hasRemainingDescendants)
            {
                continue;
            }

            failures.Add(
                new EntityUpdateResult
                {
                    UpdateState = UpdateState.Failed,
                    RequestedEntityId = folderDeleteEntityId,
                    ResultingEntityId = folderDeleteEntityId,
                    ConcurrencyTag = currentSnapshot.ConcurrencyTag,
                    ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                    CurrentEntity = currentSnapshot,
                    Errors =
                    [
                        new UpdateError
                        {
                            Message = "Folder entities with descendants cannot be deleted. Delete descendant entities in the same transaction.",
                            RelatedEntityId = folderDeleteEntityId,
                        },
                    ],
                });
        }

        return failures;
    }

    private async Task<Dictionary<EntityId, QueryEntitySnapshot>> GetLatestSnapshotsByIdAsync(
        CancellationToken cancellationToken)
    {
        var latestById = new Dictionary<EntityId, QueryEntitySnapshot>();
#pragma warning disable CS0618
        var exportResult = await this.UnderlyingDataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
        foreach (var batch in exportResult.ChangeBatches)
        {
            foreach (var entity in batch.Entities)
            {
                if (!latestById.TryGetValue(entity.EntityId, out var existing)
                    || CompareTimestamp(entity.ModifiedTime, existing.ModifiedTime) > 0)
                {
                    latestById[entity.EntityId] = entity;
                }
            }
        }

        return latestById;
    }

    private IReadOnlyCollection<JsonElement> ResolveEffectiveEntities(
        ReferenceConstraint reference,
        IReadOnlyDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        IReadOnlyDictionary<string, List<JsonElement>> requestEntitiesByName,
        IReadOnlyDictionary<string, List<JsonElement>> currentChangedEntitiesByName,
        IReadOnlyDictionary<string, List<JsonElement>> currentReferencedEntitiesByName)
    {
        if (reference.TargetEntityId is { } entityId)
        {
            if (changesByEntityId.TryGetValue(entityId, out var requestedChange))
            {
                return requestedChange.Data is { ValueKind: JsonValueKind.Object }
                    ? new[] { requestedChange.Data.Value }
                    : Array.Empty<JsonElement>();
            }

            if (currentSnapshotsById.TryGetValue(entityId, out var currentSnapshot)
                && currentSnapshot.Data is { ValueKind: JsonValueKind.Object })
            {
                return new[] { currentSnapshot.Data.Value };
            }

            return Array.Empty<JsonElement>();
        }

        if (string.IsNullOrWhiteSpace(reference.TargetEntityName))
        {
            return Array.Empty<JsonElement>();
        }

        if (requestEntitiesByName.TryGetValue(reference.TargetEntityName, out var requestedTargets))
        {
            return requestedTargets;
        }

        if (currentChangedEntitiesByName.TryGetValue(reference.TargetEntityName, out var currentChangedTargets))
        {
            return currentChangedTargets;
        }

        if (currentReferencedEntitiesByName.TryGetValue(reference.TargetEntityName, out var currentTargets))
        {
            return currentTargets;
        }

        if (reference.RequiredTypes is not null
            && reference.RequiredTypes.Contains("entity-type", StringComparer.Ordinal)
            && !reference.TargetEntityName.StartsWith("[", StringComparison.Ordinal))
        {
            var prefixedEntityTypeName = JsonSerializer.Serialize(new[] { "entity-types", reference.TargetEntityName });
            if (requestEntitiesByName.TryGetValue(prefixedEntityTypeName, out var requestedPrefixedTargets))
            {
                return requestedPrefixedTargets;
            }

            if (currentChangedEntitiesByName.TryGetValue(prefixedEntityTypeName, out var currentChangedPrefixedTargets))
            {
                return currentChangedPrefixedTargets;
            }

            if (currentReferencedEntitiesByName.TryGetValue(prefixedEntityTypeName, out var currentPrefixedTargets))
            {
                return currentPrefixedTargets;
            }
        }

        return Array.Empty<JsonElement>();
    }

    private string GetReferenceDisplay(
        ReferenceConstraint reference)
    {
        if (reference.TargetEntityId is { } targetEntityId)
        {
            return targetEntityId.ToString();
        }

        return reference.TargetEntityName ?? string.Empty;
    }

    private async Task<List<ReferenceConstraint>> ExtractReferencesAsync(
        JsonElement entityData,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        var references = new List<ReferenceConstraint>();
        references.AddRange(this.ExtractRelationshipParticipantReferences(entityData));
        references.AddRange(this.ExtractHeuristicEntityIdReferences(entityData));
        references.AddRange(await this.ExtractSchemaTypedReferencesAsync(entityData, requestSchemaEntitiesByName, cancellationToken).ConfigureAwait(false));
        return references
            .GroupBy(
                static reference => (
                    reference.TargetEntityId,
                    reference.TargetEntityName ?? string.Empty,
                    Key: string.Join("|", reference.RequiredTypes ?? Array.Empty<string>())))
            .Select(static group => group.First())
            .ToList();
    }

    private IReadOnlyCollection<ReferenceConstraint> ExtractRelationshipParticipantReferences(
        JsonElement entityData)
    {
        if (!this.IsRelationshipEntity(entityData))
        {
            return Array.Empty<ReferenceConstraint>();
        }

        return this.GetRelationshipParticipantEntityIds(entityData)
            .Select(static entityId => new ReferenceConstraint { TargetEntityId = entityId, TargetEntityName = null })
            .ToArray();
    }

    private IReadOnlyCollection<ReferenceConstraint> ExtractHeuristicEntityIdReferences(
        JsonElement entityData)
    {
        var references = new List<ReferenceConstraint>();
        this.CollectHeuristicEntityIdReferences(entityData, propertyName: null, references);

        var selfEntityId = this.ResolveEntityId(entityData);
        return references
            .Where(reference => selfEntityId is null || reference.TargetEntityId != selfEntityId.Value)
            .ToArray();
    }

    private void CollectHeuristicEntityIdReferences(
        JsonElement value,
        string? propertyName,
        ICollection<ReferenceConstraint> references)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                this.CollectHeuristicEntityIdReferences(property.Value, property.Name, references);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                this.CollectHeuristicEntityIdReferences(item, propertyName, references);
            }

            return;
        }

        if (propertyName is null)
        {
            return;
        }

        if (string.Equals(propertyName, "entity-id", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var isEntityIdField = propertyName.EndsWith("entity-id", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith("entity-ids", StringComparison.OrdinalIgnoreCase);
        if (!isEntityIdField
            || !Guid.TryParse(value.GetString(), out var targetEntityGuid))
        {
            return;
        }

        references.Add(
            new ReferenceConstraint
            {
                TargetEntityId = new EntityId(targetEntityGuid),
                TargetEntityName = null,
            });
    }

    private async Task<IReadOnlyCollection<ReferenceConstraint>> ExtractSchemaTypedReferencesAsync(
        JsonElement entityData,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        var applicableSchemas = await this.ResolveApplicableSchemasAsync(entityData, requestSchemaEntitiesByName, cancellationToken).ConfigureAwait(false);
        if (applicableSchemas.Count == 0)
        {
            return Array.Empty<ReferenceConstraint>();
        }

        var references = new List<ReferenceConstraint>();
        foreach (var applicableSchema in applicableSchemas)
        {
            if (applicableSchema.SchemaEntity is not { ValueKind: JsonValueKind.Object } schemaEntity)
            {
                continue;
            }

            var schemaNode = this.GetSchemaPayloadOrSelf(schemaEntity);
            if (schemaNode is null)
            {
                continue;
            }

            await this.CollectSchemaTypedReferencesAsync(
                entityData,
                schemaNode.Value,
                applicableSchema.SchemaReference,
                requestSchemaEntitiesByName,
                references,
                cancellationToken).ConfigureAwait(false);
        }

        return references;
    }

    private async Task CollectSchemaTypedReferencesAsync(
        JsonElement value,
        JsonElement schema,
        string schemaName,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        ICollection<ReferenceConstraint> references,
        CancellationToken cancellationToken)
    {
        var directRequiredTypes = this.GetRequiredEntityTypesFromSchema(schema);
        var resolvedSchema = await this.ResolveSchemaNodeAsync(
            schema,
            schemaName,
            requestSchemaEntitiesByName,
            cancellationToken).ConfigureAwait(false);
        if (resolvedSchema is null)
        {
            return;
        }

        var requiredTypes = directRequiredTypes
            .Concat(this.GetRequiredEntityTypesFromSchema(resolvedSchema.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requiredTypes.Length > 0)
        {
            this.AddSchemaTypedReferencesFromValue(value, resolvedSchema.Value, requiredTypes, references);
        }

        if (resolvedSchema.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Object
            && resolvedSchema.Value.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (!properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    continue;
                }

                await this.CollectSchemaTypedReferencesAsync(
                    property.Value,
                    propertySchema,
                    schemaName,
                    requestSchemaEntitiesByName,
                    references,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (value.ValueKind == JsonValueKind.Array
            && resolvedSchema.Value.TryGetProperty("items", out var itemsSchema))
        {
            foreach (var item in value.EnumerateArray())
            {
                await this.CollectSchemaTypedReferencesAsync(
                    item,
                    itemsSchema,
                    schemaName,
                    requestSchemaEntitiesByName,
                    references,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var compositionalKeyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!resolvedSchema.Value.TryGetProperty(compositionalKeyword, out var schemas)
                || schemas.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var nestedSchema in schemas.EnumerateArray())
            {
                if (nestedSchema.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                await this.CollectSchemaTypedReferencesAsync(
                    value,
                    nestedSchema,
                    schemaName,
                    requestSchemaEntitiesByName,
                    references,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void AddSchemaTypedReferencesFromValue(
        JsonElement value,
        JsonElement resolvedSchema,
        IReadOnlyCollection<string> requiredTypes,
        ICollection<ReferenceConstraint> references)
    {
        var singleReference = value.TryReadEntityReference();
        if (singleReference is not null && value.ValueKind == JsonValueKind.String)
        {
            this.AddReferenceConstraint(singleReference.Value, requiredTypes, references);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var isCollectionSchema = this.IsCollectionReferenceSchema(resolvedSchema);
        if (!isCollectionSchema
            && singleReference is not null)
        {
            this.AddReferenceConstraint(singleReference.Value, requiredTypes, references);
            return;
        }

        foreach (var item in value.EnumerateArray())
        {
            var itemReference = item.TryReadEntityReference();
            if (itemReference is null)
            {
                continue;
            }

            this.AddReferenceConstraint(itemReference.Value, requiredTypes, references);
        }
    }

    private void AddReferenceConstraint(
        EntityReference reference,
        IReadOnlyCollection<string> requiredTypes,
        ICollection<ReferenceConstraint> references)
    {
        if (reference.EntityId is EntityId entityId)
        {
            references.Add(
                new ReferenceConstraint
                {
                    TargetEntityId = entityId,
                    TargetEntityName = null,
                    RequiredTypes = requiredTypes,
                });
            return;
        }

        if (reference.EntityName is not EntityName entityName)
        {
            return;
        }

        var targetName = reference.IsNameArray
            ? JsonSerializer.Serialize(entityName.Components)
            : entityName.Components[0];
        references.Add(
            new ReferenceConstraint
            {
                TargetEntityId = null,
                TargetEntityName = targetName,
                RequiredTypes = requiredTypes,
            });
    }

    private bool IsCollectionReferenceSchema(
        JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            && string.Equals(typeElement.GetString(), "array", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private async Task<JsonElement?> ResolveSchemaNodeAsync(
        JsonElement schema,
        string schemaName,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return schema;
        }

        if (!schema.TryGetProperty("$ref", out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(referenceElement.GetString()))
        {
            return schema;
        }

        var referenceValue = referenceElement.GetString()!;
        if (referenceValue.StartsWith('#'))
        {
            var localResolution = this.ResolveJsonPointer(schema, referenceValue);
            if (localResolution is not null)
            {
                return localResolution;
            }

            var rootSchema = await this.ResolveSchemaAsync(
                schemaName,
                requestSchemaEntitiesByName,
                cancellationToken).ConfigureAwait(false);
            if (rootSchema is null)
            {
                return null;
            }

            var rootSchemaNode = this.GetSchemaPayloadOrSelf(rootSchema.Value);
            if (rootSchemaNode is null)
            {
                return null;
            }

            return this.ResolveJsonPointer(rootSchemaNode.Value, referenceValue);
        }

        var hashIndex = referenceValue.IndexOf('#');
        var referencedSchemaName = hashIndex >= 0
            ? referenceValue.Substring(0, hashIndex)
            : referenceValue;
        var fragment = hashIndex >= 0
            ? referenceValue.Substring(hashIndex)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(referencedSchemaName))
        {
            referencedSchemaName = schemaName;
        }

        var referencedSchema = await this.ResolveSchemaAsync(
            referencedSchemaName,
            requestSchemaEntitiesByName,
            cancellationToken).ConfigureAwait(false);
        if (referencedSchema is null)
        {
            return null;
        }

        var referencedSchemaNode = this.GetSchemaPayloadOrSelf(referencedSchema.Value);
        if (referencedSchemaNode is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(fragment))
        {
            return referencedSchemaNode;
        }

        return this.ResolveJsonPointer(referencedSchemaNode.Value, fragment);
    }

    private JsonElement? ResolveJsonPointer(
        JsonElement root,
        string pointer)
    {
        if (pointer == "#")
        {
            return root;
        }

        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var rawSegment in pointer.Substring(2).Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return null;
                }

                continue;
            }

            if (current.ValueKind != JsonValueKind.Array
                || !int.TryParse(segment, out var index)
                || index < 0
                || index >= current.GetArrayLength())
            {
                return null;
            }

            current = current[index];
        }

        return current;
    }

    private IReadOnlyCollection<string> GetRequiredEntityTypesFromSchema(
        JsonElement schemaNode)
    {
        if (schemaNode.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        if (!schemaNode.TryGetProperty("x-entity-types", out var xEntityTypes)
            || xEntityTypes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return xEntityTypes.EnumerateArray()
            .Where(static element => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
            .Select(static element => element.GetString()!)
            .ToArray();
    }

    private JsonElement? GetSchemaPayloadOrSelf(
        JsonElement schemaEntity)
    {
        if (schemaEntity.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!schemaEntity.TryGetProperty("schema", out var schemaPayload))
        {
            return schemaEntity;
        }

        if (schemaPayload.ValueKind == JsonValueKind.Object)
        {
            return schemaPayload;
        }

        if (schemaPayload.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(schemaPayload.GetString()))
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            $$"""
            {
              "$ref": "{{schemaPayload.GetString()}}"
            }
            """);
        return document.RootElement.Clone();
    }

    private IReadOnlyDictionary<string, List<JsonElement>> GetEntitiesByName(
        IEnumerable<JsonElement> entities)
    {
        var entitiesByName = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var entityData in entities)
        {
            foreach (var name in this.GetEntityNames(entityData))
            {
                if (!entitiesByName.TryGetValue(name, out var namedEntities))
                {
                    namedEntities = new List<JsonElement>();
                    entitiesByName[name] = namedEntities;
                }

                namedEntities.Add(entityData);
            }
        }

        return entitiesByName;
    }

    private async Task<IReadOnlyDictionary<string, List<JsonElement>>> GetCurrentEntitiesByNameAsync(
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        }

        var latestEntitiesById = new Dictionary<EntityId, JsonElement?>();
#pragma warning disable CS0618
        var exportResult = await this.UnderlyingDataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
        foreach (var batch in exportResult.ChangeBatches)
        {
            foreach (var entity in batch.Entities)
            {
                latestEntitiesById[entity.EntityId] = entity.Data;
            }
        }

        var entitiesByName = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var latestEntityData in latestEntitiesById.Values)
        {
            if (latestEntityData is not { ValueKind: JsonValueKind.Object } entityData)
            {
                continue;
            }

            foreach (var name in this.GetEntityNames(entityData))
            {
                if (!entitiesByName.TryGetValue(name, out var namedEntities))
                {
                    namedEntities = new List<JsonElement>();
                    entitiesByName[name] = namedEntities;
                }

                namedEntities.Add(entityData);
            }
        }

        return entitiesByName;
    }

    private IReadOnlyDictionary<EntityId, int> GetManagedReferenceCounts(
        JsonElement? entityData)
    {
        if (entityData is not { ValueKind: JsonValueKind.Object })
        {
            return new Dictionary<EntityId, int>();
        }

        var counts = new Dictionary<EntityId, int>();
        this.CollectManagedReferenceCounts(entityData.Value, propertyName: null, counts);
        return counts;
    }

    private void CollectManagedReferenceCounts(
        JsonElement value,
        string? propertyName,
        IDictionary<EntityId, int> counts)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (string.Equals(propertyName, "participants", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var property in value.EnumerateObject())
                {
                    this.CollectParticipantReferenceCounts(property.Value, counts);
                }

                return;
            }

            foreach (var property in value.EnumerateObject())
            {
                this.CollectManagedReferenceCounts(property.Value, property.Name, counts);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                this.CollectManagedReferenceCounts(item, propertyName, counts);
            }

            return;
        }

        if (propertyName is null
            || propertyName.Equals("entity-id", StringComparison.OrdinalIgnoreCase)
            || !propertyName.EndsWith("entity-id", StringComparison.OrdinalIgnoreCase)
            && !propertyName.EndsWith("entity-ids", StringComparison.OrdinalIgnoreCase)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var targetEntityGuid))
        {
            return;
        }

        var targetEntityId = new EntityId(targetEntityGuid);
        counts.TryGetValue(targetEntityId, out var existingCount);
        counts[targetEntityId] = existingCount + 1;
    }

    private void CollectParticipantReferenceCounts(
        JsonElement value,
        IDictionary<EntityId, int> counts)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                this.CollectParticipantReferenceCounts(property.Value, counts);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                this.CollectParticipantReferenceCounts(item, counts);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var participantGuid))
        {
            return;
        }

        var participantEntityId = new EntityId(participantGuid);
        counts.TryGetValue(participantEntityId, out var existingParticipantCount);
        counts[participantEntityId] = existingParticipantCount + 1;
    }

    private EntityId GetManagedReferenceRelationshipEntityId(
        EntityId sourceEntityId,
        EntityId targetEntityId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{sourceEntityId}|{targetEntityId}");
        var hash = SHA256.HashData(bytes);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new EntityId(new Guid(guidBytes));
    }

    private JsonElement CreateManagedReferenceRelationshipEntity(
        EntityId relationshipEntityId,
        EntityId sourceEntityId,
        EntityId targetEntityId)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipEntityId}}",
              "entity-types": ["relationship", "reference"],
              "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/reference.json",
              "participants": {
                "source": "{{sourceEntityId}}",
                "target": "{{targetEntityId}}"
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private bool IsRelationshipEntity(
        JsonElement entityData)
    {
        return this.GetEntityTypeNames(entityData).Contains(RelationshipType)
            || this.HasSourceTargetRelationshipShape(entityData);
    }

    private bool IsReferenceRelationshipEntity(
        JsonElement entityData)
    {
        return this.HasSourceTargetRelationshipShape(entityData);
    }

    private IReadOnlyCollection<EntityId> GetRelationshipParticipantEntityIds(
        JsonElement entityData)
    {
        if (!RelationshipParticipantIdExtractor.TryGetRelationshipParticipantIds(entityData, out var participantIds))
        {
            return Array.Empty<EntityId>();
        }

        return participantIds;
    }

    private string? GetRelationshipKey(
        JsonElement relationshipData)
    {
        if (!this.IsRelationshipEntity(relationshipData))
        {
            return null;
        }

        var participantIds = this.GetRelationshipParticipantEntityIds(relationshipData)
            .Select(static entityId => entityId.ToString())
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (participantIds.Length == 0)
        {
            return null;
        }

        var typeNames = this.GetEntityTypeNames(relationshipData)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (typeNames.Length == 0 && this.HasSourceTargetRelationshipShape(relationshipData))
        {
            typeNames = new[] { ReferenceRelationshipType };
        }
        var roles = this.GetRelationshipRoles(relationshipData);

        return $"{string.Join("|", typeNames)}::{string.Join("|", participantIds)}::{string.Join("|", roles)}";
    }

    private bool HasSourceTargetRelationshipShape(
        JsonElement entityData)
    {
        return entityData.ValueKind == JsonValueKind.Object
            && entityData.TryGetProperty("participants", out var participants)
            && participants.ValueKind == JsonValueKind.Object;
    }

    private IReadOnlyCollection<string> GetRelationshipRoles(
        JsonElement relationshipData)
    {
        if (!relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        return participants.EnumerateObject()
            .Select(static property => property.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private bool IsFolderEntity(
        JsonElement entityData)
    {
        return this.GetEntityTypeNames(entityData).Contains(FolderType, StringComparer.Ordinal);
    }

    private static string SerializeEntityName(
        EntityName name)
    {
        return JsonSerializer.Serialize(name.Components);
    }

    private static bool IsDescendantName(
        EntityName candidate,
        EntityName parent)
    {
        return candidate.Components.Length > parent.Components.Length
            && candidate.Components.Take(parent.Components.Length).SequenceEqual(parent.Components, StringComparer.Ordinal);
    }

    private static EntityId GetFolderEntityId(
        EntityName folderName)
    {
        var bytes = Encoding.UTF8.GetBytes($"folder::{SerializeEntityName(folderName)}");
        var hash = SHA256.HashData(bytes);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new EntityId(new Guid(guidBytes));
    }

    private static JsonElement CreateFolderEntityData(
        EntityId entityId,
        EntityName folderName)
    {
        var folderTitle = folderName.Components.Length == 0
            ? "root"
            : folderName.Components[^1];
        var serializedTitle = JsonSerializer.Serialize(folderTitle);
        var serializedName = JsonSerializer.Serialize(new[] { folderName.Components });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["folder"],
              "$schema": "{{FolderSchema}}",
              "names": {{serializedName}},
              "display-name": { "default": {{serializedTitle}} }
            }
            """);
        return document.RootElement.Clone();
    }

    private IReadOnlyCollection<EntityName> GetEntityNameValues(
        JsonElement entityData)
    {
        if (!entityData.TryGetProperty("names", out var namesElement)
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

    private static int CompareTimestamp(
        Timestamp left,
        Timestamp right)
    {
        var timeComparison = left.DateTime.CompareTo(right.DateTime);
        return timeComparison != 0
            ? timeComparison
            : StringComparer.Ordinal.Compare(left.ChangeId, right.ChangeId);
    }

    private void CollectEntityIds(
        JsonElement value,
        ICollection<EntityId> ids)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                this.CollectEntityIds(property.Value, ids);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                this.CollectEntityIds(item, ids);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var guid))
        {
            return;
        }

        ids.Add(new EntityId(guid));
    }

    private void UpsertChange(
        IDictionary<EntityId, EntityChange> changesByEntityId,
        IDictionary<EntityId, OrderedChange> orderedChangesByEntityId,
        EntityId entityId,
        EntityChange change,
        ref int nextOrder)
    {
        changesByEntityId[entityId] = change;
        if (orderedChangesByEntityId.TryGetValue(entityId, out var existingOrderedChange))
        {
            orderedChangesByEntityId[entityId] = existingOrderedChange with { Change = change };
            return;
        }

        orderedChangesByEntityId[entityId] = new OrderedChange
        {
            Order = nextOrder++,
            Change = change,
        };
    }

    private EntityId? ResolveEntityId(
        EntityChange change)
    {
        if (change.EntityId is not null)
        {
            return change.EntityId.Value;
        }

        return change.Data is null
            ? null
            : this.ResolveEntityId(change.Data.Value);
    }

    private EntityId? ResolveEntityId(
        JsonElement entityData)
    {
        if (entityData.ValueKind != JsonValueKind.Object
            || !entityData.TryGetProperty("entity-id", out var entityIdElement)
            || entityIdElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entityIdElement.GetString(), out var entityGuid))
        {
            return null;
        }

        return new EntityId(entityGuid);
    }

    private async Task<Dictionary<EntityId, EntitySnapshot>> GetCurrentSnapshotsByIdAsync(
        IEnumerable<EntityId> entityIds,
        bool includeRelationships,
        CancellationToken cancellationToken)
    {
        var ids = entityIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<EntityId, EntitySnapshot>();
        }

        var getResult = await this.UnderlyingDataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = ids.Select(static id => new GetEntityRequest { EntityId = id }).ToArray(),
                RelationshipsToReturn = includeRelationships ? Array.Empty<GetRelationshipRequest>() : null,
                Timestamps = new Timestamp?[] { null },
            },
            cancellationToken).ConfigureAwait(false);

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .ToDictionary(static snapshot => snapshot.EntityId, static snapshot => snapshot);
    }

    private sealed record OrderedChange
    {
        public required int Order { get; init; }

        public required EntityChange Change { get; init; }
    }

    private sealed record RewriteState
    {
        public required IReadOnlyCollection<EntityChange> Changes { get; init; }

        public required IReadOnlyCollection<EntityUpdateResult> Failures { get; init; }
    }

    private sealed record ReferenceConstraint
    {
        public EntityId? TargetEntityId { get; init; }

        public string? TargetEntityName { get; init; }

        public IReadOnlyCollection<string>? RequiredTypes { get; init; }
    }
}
