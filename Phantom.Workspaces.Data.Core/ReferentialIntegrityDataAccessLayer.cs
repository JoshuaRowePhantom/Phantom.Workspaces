using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs referential integrity validation on data being updated on an underlying IDataAccessLayer.
/// This layer also manages synthetic "reference" relationship entities for object-property references.
/// </summary>
public class ReferentialIntegrityDataAccessLayer : BaseUpdateProcessingDataAccessLayer
{
    private const string ReferenceRelationshipType = "reference";
    private const string RelationshipType = "relationship";
    private const string JsonSchemaType = "json-schema";

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

        var rewriteState = await this.BuildRewrittenChangesAsync(request, cancellationToken);
        if (rewriteState.Failures.Count > 0)
        {
            return new UpdateResult(rewriteState.Failures);
        }

        return await this.UnderlyingDataAccessLayer.UpdateAsync(
            new UpdateRequest(request.UpdateMetadata, rewriteState.Changes),
            cancellationToken);
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
                unresolvedChanges.Add(new OrderedChange(order++, change));
                continue;
            }

            if (!orderedChangesByEntityId.TryGetValue(resolvedEntityId.Value, out var existingOrderedChange))
            {
                orderedChangesByEntityId[resolvedEntityId.Value] = new OrderedChange(order++, change with { EntityId = resolvedEntityId.Value });
                continue;
            }

            orderedChangesByEntityId[resolvedEntityId.Value] = new OrderedChange(existingOrderedChange.Order, change with { EntityId = resolvedEntityId.Value });
        }

        var changesByEntityId = orderedChangesByEntityId.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Change);

        var currentSnapshotsById = await this.GetCurrentSnapshotsByIdAsync(
            changesByEntityId.Keys,
            includeRelationships: true,
            cancellationToken);

        this.ApplyManagedReferenceRelationshipChanges(changesByEntityId, currentSnapshotsById, orderedChangesByEntityId, ref order);
        this.ApplyRelationshipDeleteCascade(changesByEntityId, currentSnapshotsById, orderedChangesByEntityId, ref order);
        await this.ApplyDuplicateRelationshipCoalescingAsync(changesByEntityId, currentSnapshotsById, orderedChangesByEntityId, cancellationToken);

        var validationFailures = await this.ValidateReferencesAsync(
            changesByEntityId,
            currentSnapshotsById,
            cancellationToken);
        if (validationFailures.Count > 0)
        {
            return new RewriteState(Array.Empty<EntityChange>(), validationFailures);
        }

        var rewrittenChanges = unresolvedChanges
            .Concat(orderedChangesByEntityId.Values)
            .OrderBy(static orderedChange => orderedChange.Order)
            .Select(static orderedChange => orderedChange.Change)
            .ToArray();
        return new RewriteState(rewrittenChanges, Array.Empty<EntityUpdateResult>());
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
                var participantSnapshot = await this.EnsureEntitySnapshotWithRelationshipsAsync(participant, currentSnapshotsById, cancellationToken);
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
            cancellationToken);
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
                        new EntityChange(
                            relationshipEntityId,
                            currentRelationship?.ConcurrencyTag,
                            this.CreateManagedReferenceRelationshipEntity(
                                relationshipEntityId,
                                entityId,
                                targetEntityId),
                            EntityChangeMode.Replace),
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
                    new EntityChange(
                        relationshipEntityId,
                        currentRelationshipToDelete?.ConcurrencyTag,
                        null,
                        EntityChangeMode.Replace),
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
                        new EntityChange(
                            relationship.EntityId,
                            relationship.ConcurrencyTag,
                            null,
                            EntityChangeMode.Replace),
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
                    new EntityChange(
                        pair.Key,
                        currentRelationship?.ConcurrencyTag ?? pair.Value.ConcurrencyTag,
                        null,
                        EntityChangeMode.Replace),
                    ref nextOrder);
            }
        }
    }

    private async Task<IReadOnlyCollection<EntityUpdateResult>> ValidateReferencesAsync(
        IReadOnlyDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        CancellationToken cancellationToken)
    {
        var requestSchemaEntitiesByName = this.GetSchemaEntitiesFromChanges(changesByEntityId.Values);
        var referencedEntityIds = new HashSet<EntityId>();
        var referencesBySource = new Dictionary<EntityId, List<ReferenceConstraint>>();

        foreach (var pair in changesByEntityId)
        {
            if (pair.Value.Data is null)
            {
                continue;
            }

            var sourceEntityId = pair.Key;
            var sourceData = pair.Value.Data.Value;
            var references = this.ExtractReferences(
                sourceData,
                requestSchemaEntitiesByName,
                cancellationToken);
            referencesBySource[sourceEntityId] = references;
            foreach (var reference in references)
            {
                referencedEntityIds.Add(reference.TargetEntityId);
            }
        }

        var remainingReferencedIds = referencedEntityIds
            .Where(entityId => !changesByEntityId.ContainsKey(entityId))
            .ToArray();
        var currentReferencedSnapshotsById = await this.GetCurrentSnapshotsByIdAsync(
            remainingReferencedIds,
            includeRelationships: false,
            cancellationToken);

        var failures = new List<EntityUpdateResult>();
        foreach (var pair in referencesBySource)
        {
            var sourceEntityId = pair.Key;
            var sourceChange = changesByEntityId[sourceEntityId];
            var errors = new List<UpdateError>();

            foreach (var reference in pair.Value)
            {
                var targetExists = this.TryResolveEffectiveEntity(
                    reference.TargetEntityId,
                    changesByEntityId,
                    currentReferencedSnapshotsById,
                    out var targetData);
                if (!targetExists || targetData is null)
                {
                    errors.Add(
                        new UpdateError(
                            $"Referenced entity '{reference.TargetEntityId.Value:D}' does not exist.",
                            reference.TargetEntityId));
                    continue;
                }

                if (reference.RequiredTypes is null || reference.RequiredTypes.Count == 0)
                {
                    continue;
                }

                var targetTypes = this.GetEntityTypeNames(targetData.Value);
                if (!reference.RequiredTypes.Any(targetTypes.Contains))
                {
                    errors.Add(
                        new UpdateError(
                            $"Referenced entity '{reference.TargetEntityId.Value:D}' does not match required types: {string.Join(", ", reference.RequiredTypes)}.",
                            reference.TargetEntityId));
                }
            }

            if (errors.Count == 0)
            {
                continue;
            }

            currentSnapshotsById.TryGetValue(sourceEntityId, out var currentEntity);
            failures.Add(
                new EntityUpdateResult(
                    UpdateState.Failed,
                    sourceEntityId,
                    sourceEntityId,
                    currentEntity?.ConcurrencyTag,
                    ConcurrencyMatchState.NotMatched,
                    currentEntity,
                    errors));
        }

        return failures;
    }

    private bool TryResolveEffectiveEntity(
        EntityId entityId,
        IReadOnlyDictionary<EntityId, EntityChange> changesByEntityId,
        IReadOnlyDictionary<EntityId, EntitySnapshot> currentSnapshotsById,
        out JsonElement? data)
    {
        if (changesByEntityId.TryGetValue(entityId, out var requestedChange))
        {
            data = requestedChange.Data;
            return requestedChange.Data is not null;
        }

        if (currentSnapshotsById.TryGetValue(entityId, out var currentSnapshot))
        {
            data = currentSnapshot.Data;
            return currentSnapshot.Data is not null;
        }

        data = null;
        return false;
    }

    private List<ReferenceConstraint> ExtractReferences(
        JsonElement entityData,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        var references = new List<ReferenceConstraint>();
        references.AddRange(this.ExtractRelationshipParticipantReferences(entityData));
        references.AddRange(this.ExtractHeuristicEntityIdReferences(entityData));
        references.AddRange(this.ExtractSchemaTypedReferences(entityData, requestSchemaEntitiesByName, cancellationToken));
        return references
            .GroupBy(static reference => (reference.TargetEntityId, Key: string.Join("|", reference.RequiredTypes ?? Array.Empty<string>())))
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
            .Select(static entityId => new ReferenceConstraint(entityId, null))
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

        if (string.Equals(propertyName, "entity-id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(propertyName, "related-entity-ids", StringComparison.OrdinalIgnoreCase))
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

        references.Add(new ReferenceConstraint(new EntityId(targetEntityGuid), null));
    }

    private IReadOnlyCollection<ReferenceConstraint> ExtractSchemaTypedReferences(
        JsonElement entityData,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        if (!entityData.TryGetProperty("$schema", out var schemaProperty)
            || schemaProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(schemaProperty.GetString()))
        {
            return Array.Empty<ReferenceConstraint>();
        }

        var rootSchemaName = schemaProperty.GetString()!;
        var rootSchema = this.ResolveSchemaByName(rootSchemaName, requestSchemaEntitiesByName, cancellationToken);
        if (rootSchema is null)
        {
            return Array.Empty<ReferenceConstraint>();
        }

        var references = new List<ReferenceConstraint>();
        this.CollectSchemaTypedReferences(
            entityData,
            rootSchema.Value,
            rootSchemaName,
            requestSchemaEntitiesByName,
            references,
            cancellationToken);
        return references;
    }

    private void CollectSchemaTypedReferences(
        JsonElement value,
        JsonElement schema,
        string schemaName,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        ICollection<ReferenceConstraint> references,
        CancellationToken cancellationToken)
    {
        var directRequiredTypes = this.GetRequiredEntityTypesFromSchema(schema);
        var resolvedSchema = this.ResolveSchemaNode(schema, schemaName, requestSchemaEntitiesByName, cancellationToken);
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
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var targetEntityGuid))
            {
                references.Add(new ReferenceConstraint(new EntityId(targetEntityGuid), requiredTypes));
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var targetGuid))
                    {
                        references.Add(new ReferenceConstraint(new EntityId(targetGuid), requiredTypes));
                    }
                }
            }
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

                this.CollectSchemaTypedReferences(
                    property.Value,
                    propertySchema,
                    schemaName,
                    requestSchemaEntitiesByName,
                    references,
                    cancellationToken);
            }
        }

        if (value.ValueKind == JsonValueKind.Array
            && resolvedSchema.Value.TryGetProperty("items", out var itemsSchema))
        {
            foreach (var item in value.EnumerateArray())
            {
                this.CollectSchemaTypedReferences(
                    item,
                    itemsSchema,
                    schemaName,
                    requestSchemaEntitiesByName,
                    references,
                    cancellationToken);
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
                this.CollectSchemaTypedReferences(
                    value,
                    nestedSchema,
                    schemaName,
                    requestSchemaEntitiesByName,
                    references,
                    cancellationToken);
            }
        }
    }

    private JsonElement? ResolveSchemaNode(
        JsonElement schema,
        string schemaName,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        if (!schema.TryGetProperty("$ref", out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(referenceElement.GetString()))
        {
            return schema;
        }

        var referenceValue = referenceElement.GetString()!;
        if (referenceValue.StartsWith('#'))
        {
            return this.ResolveJsonPointer(schema, referenceValue);
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

        var referencedSchema = this.ResolveSchemaByName(referencedSchemaName, requestSchemaEntitiesByName, cancellationToken);
        if (referencedSchema is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(fragment))
        {
            return referencedSchema;
        }

        return this.ResolveJsonPointer(referencedSchema.Value, fragment);
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
        if (!schemaNode.TryGetProperty("x-entity-type", out var xEntityType))
        {
            return Array.Empty<string>();
        }

        if (xEntityType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(xEntityType.GetString()))
        {
            return new[] { xEntityType.GetString()! };
        }

        if (xEntityType.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return xEntityType.EnumerateArray()
            .Where(static element => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
            .Select(static element => element.GetString()!)
            .ToArray();
    }

    private JsonElement? ResolveSchemaByName(
        string schemaName,
        IReadOnlyDictionary<string, JsonElement> requestSchemaEntitiesByName,
        CancellationToken cancellationToken)
    {
        if (requestSchemaEntitiesByName.TryGetValue(schemaName, out var requestSchema))
        {
            return requestSchema;
        }

        var getResult = this.UnderlyingDataAccessLayer.GetAsync(
            new GetRequest(
                new[]
                {
                    new GetEntityRequest(
                        null,
                        new EntityName(schemaName),
                        null,
                        null),
                },
                null,
                new Timestamp?[] { null }),
            cancellationToken).GetAwaiter().GetResult();

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .FirstOrDefault(static data => data is { ValueKind: JsonValueKind.Object });
    }

    private IReadOnlyDictionary<string, JsonElement> GetSchemaEntitiesFromChanges(
        IEnumerable<EntityChange> changes)
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.Data is not { } data
                || !this.IsSchemaEntity(data))
            {
                continue;
            }

            foreach (var name in this.GetEntityNames(data))
            {
                schemas[name] = data;
            }
        }

        return schemas;
    }

    private bool IsSchemaEntity(
        JsonElement entityData)
    {
        return this.GetEntityTypeNames(entityData).Contains(JsonSchemaType);
    }

    private IReadOnlyCollection<string> GetEntityNames(
        JsonElement entityData)
    {
        if (!entityData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return namesElement.EnumerateArray()
            .Where(static name => name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            .Select(static name => name.GetString()!)
            .ToArray();
    }

    private HashSet<string> GetEntityTypeNames(
        JsonElement entityData)
    {
        if (!entityData.TryGetProperty("entity-types", out var typeNames)
            || typeNames.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return typeNames.EnumerateArray()
            .Where(static typeName => typeName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(typeName.GetString()))
            .Select(static typeName => typeName.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
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
            || propertyName.Equals("related-entity-ids", StringComparison.OrdinalIgnoreCase)
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

    private EntityId GetManagedReferenceRelationshipEntityId(
        EntityId sourceEntityId,
        EntityId targetEntityId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{sourceEntityId.Value:D}|{targetEntityId.Value:D}");
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
              "entity-id": "{{relationshipEntityId.Value:D}}",
              "entity-types": ["relationship", "reference"],
              "names": ["reference:{{sourceEntityId.Value:D}}:{{targetEntityId.Value:D}}"],
              "related-entity-ids": ["{{sourceEntityId.Value:D}}", "{{targetEntityId.Value:D}}"],
              "relationship-roles": ["source", "destination"]
            }
            """);
        return document.RootElement.Clone();
    }

    private bool IsRelationshipEntity(
        JsonElement entityData)
    {
        return this.GetEntityTypeNames(entityData).Contains(RelationshipType);
    }

    private bool IsReferenceRelationshipEntity(
        JsonElement entityData)
    {
        var typeNames = this.GetEntityTypeNames(entityData);
        return typeNames.Contains(RelationshipType) && typeNames.Contains(ReferenceRelationshipType);
    }

    private IReadOnlyCollection<EntityId> GetRelationshipParticipantEntityIds(
        JsonElement entityData)
    {
        if (!entityData.TryGetProperty("related-entity-ids", out var participantIds)
            || participantIds.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<EntityId>();
        }

        return participantIds.EnumerateArray()
            .Where(static participantId => participantId.ValueKind == JsonValueKind.String && Guid.TryParse(participantId.GetString(), out _))
            .Select(static participantId => new EntityId(Guid.Parse(participantId.GetString()!)))
            .ToArray();
    }

    private string? GetRelationshipKey(
        JsonElement relationshipData)
    {
        if (!this.IsRelationshipEntity(relationshipData))
        {
            return null;
        }

        var participantIds = this.GetRelationshipParticipantEntityIds(relationshipData)
            .Select(static entityId => entityId.Value.ToString("D"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (participantIds.Length == 0)
        {
            return null;
        }

        var typeNames = this.GetEntityTypeNames(relationshipData)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var roles = relationshipData.TryGetProperty("relationship-roles", out var roleElement)
            && roleElement.ValueKind == JsonValueKind.Array
                ? roleElement.EnumerateArray()
                    .Where(static role => role.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(role.GetString()))
                    .Select(static role => role.GetString()!)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();

        return $"{string.Join("|", typeNames)}::{string.Join("|", participantIds)}::{string.Join("|", roles)}";
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

        orderedChangesByEntityId[entityId] = new OrderedChange(nextOrder++, change);
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
            new GetRequest(
                ids.Select(static id => new GetEntityRequest(id, null, null, null)).ToArray(),
                includeRelationships ? Array.Empty<GetRelationshipRequest>() : null,
                new Timestamp?[] { null }),
            cancellationToken);

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .ToDictionary(static snapshot => snapshot.EntityId, static snapshot => snapshot);
    }

    private sealed record OrderedChange(
        int Order,
        EntityChange Change);

    private sealed record RewriteState(
        IReadOnlyCollection<EntityChange> Changes,
        IReadOnlyCollection<EntityUpdateResult> Failures);

    private sealed record ReferenceConstraint(
        EntityId TargetEntityId,
        IReadOnlyCollection<string>? RequiredTypes);
}
