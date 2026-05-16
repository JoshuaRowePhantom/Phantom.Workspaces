using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Processes JSON Patch update operations on top of an underlying data access layer.
/// The underlying data access layer receives one coalesced replace operation per entity.
/// </summary>
public class MergeProcessingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
{
    public MergeProcessingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
        : base(underlyingDataAccessLayer)
    {
    }

    public override Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.UpdateInternalAsync(request, cancellationToken);
    }

    private async Task<UpdateResult> UpdateInternalAsync(
        UpdateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var failures = new List<EntityUpdateResult>();
        var idsToLoad = request.Changes
            .Select(this.ResolveEntityId)
            .Where(static entityId => entityId is not null)
            .Select(static entityId => entityId!.Value)
            .ToHashSet();
        var snapshotsById = await this.GetCurrentSnapshotsByIdAsync(idsToLoad, cancellationToken);

        var statesById = new Dictionary<EntityId, CoalescedEntityState>();
        var coalescedOrder = new List<EntityId>();
        var passthroughChanges = new List<EntityChange>();

        foreach (var change in request.Changes)
        {
            var entityId = this.ResolveEntityId(change);
            if (entityId is null)
            {
                if (change.EntityChangeMode == EntityChangeMode.JsonPatch)
                {
                    failures.Add(
                        new EntityUpdateResult(
                            UpdateState.Failed,
                            default,
                            default,
                            null,
                            ConcurrencyMatchState.NotMatched,
                            null,
                            new[] { new UpdateError("JsonPatch updates require an entity-id.", null) }));
                    continue;
                }

                passthroughChanges.Add(change with { EntityChangeMode = EntityChangeMode.Replace });
                continue;
            }

            if (!statesById.TryGetValue(entityId.Value, out var state))
            {
                snapshotsById.TryGetValue(entityId.Value, out var snapshot);
                state = new CoalescedEntityState(
                    entityId.Value,
                    snapshot,
                    snapshot?.Data);
                statesById[entityId.Value] = state;
                coalescedOrder.Add(entityId.Value);
            }

            if (!this.TryMergeConcurrencyTag(state, change.ConcurrencyTag, out var concurrencyError))
            {
                failures.Add(this.CreateFailureResult(state, concurrencyError!));
                continue;
            }

            if (change.EntityChangeMode == EntityChangeMode.JsonPatch)
            {
                if (state.WorkingData is null)
                {
                    failures.Add(
                        new EntityUpdateResult(
                            UpdateState.Failed,
                            entityId.Value,
                            entityId.Value,
                            state.CurrentTag,
                            ConcurrencyMatchState.NotMatched,
                            state.CurrentSnapshot,
                            new[] { new UpdateError("JsonPatch target entity does not exist.", entityId.Value) }));
                    continue;
                }

                if (change.Data is null || change.Data.Value.ValueKind != JsonValueKind.Array)
                {
                    failures.Add(
                        new EntityUpdateResult(
                            UpdateState.Failed,
                            entityId.Value,
                            entityId.Value,
                            state.CurrentTag,
                            ConcurrencyMatchState.NotMatched,
                            state.CurrentSnapshot,
                            new[] { new UpdateError("JsonPatch data must be an array of operations.", entityId.Value) }));
                    continue;
                }

                if (!this.TryApplyJsonPatch(state.WorkingData.Value, change.Data.Value, out var patchedData, out var patchError))
                {
                    failures.Add(
                        new EntityUpdateResult(
                            UpdateState.Failed,
                            entityId.Value,
                            entityId.Value,
                            state.CurrentTag,
                            ConcurrencyMatchState.NotMatched,
                            state.CurrentSnapshot,
                            new[] { new UpdateError(patchError ?? "JsonPatch failed.", entityId.Value) }));
                    continue;
                }

                state.WorkingData = patchedData;
                continue;
            }

            state.WorkingData = change.Data?.Clone();
        }

        if (failures.Count > 0)
        {
            return new UpdateResult(failures);
        }

        foreach (var state in statesById.Values)
        {
            if (!state.ExistsInStore)
            {
                continue;
            }

            if (state.RequestedTag is null)
            {
                failures.Add(this.CreateFailureResult(state, "Concurrency tag is required."));
                continue;
            }

            if (state.CurrentTag != state.RequestedTag.Value)
            {
                failures.Add(this.CreateFailureResult(state, "Concurrency tag does not match."));
            }
        }

        if (failures.Count > 0)
        {
            return new UpdateResult(failures);
        }

        var processedChanges = new List<EntityChange>(coalescedOrder.Count + passthroughChanges.Count);
        foreach (var entityId in coalescedOrder)
        {
            var state = statesById[entityId];
            processedChanges.Add(
                new EntityChange(
                    entityId,
                    state.RequestedTag,
                    state.WorkingData,
                    EntityChangeMode.Replace));
        }

        processedChanges.AddRange(passthroughChanges);

        return await this.UnderlyingDataAccessLayer.UpdateAsync(
            new UpdateRequest(request.UpdateMetadata, processedChanges),
            cancellationToken);
    }

    private bool TryMergeConcurrencyTag(
        CoalescedEntityState state,
        ConcurrencyTag? candidateTag,
        out string? error)
    {
        if (candidateTag is null)
        {
            error = null;
            return true;
        }

        if (state.RequestedTag is null)
        {
            state.RequestedTag = candidateTag.Value;
            error = null;
            return true;
        }

        if (state.RequestedTag.Value == candidateTag.Value)
        {
            error = null;
            return true;
        }

        error = "Multiple updates for the same entity provided conflicting concurrency tags.";
        return false;
    }

    private EntityUpdateResult CreateFailureResult(
        CoalescedEntityState state,
        string message)
    {
        return new EntityUpdateResult(
            UpdateState.Failed,
            state.EntityId,
            state.EntityId,
            state.CurrentTag,
            ConcurrencyMatchState.NotMatched,
            state.CurrentSnapshot,
            new[] { new UpdateError(message, state.EntityId) });
    }

    private EntityId? ResolveEntityId(
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

        if (!change.Data.Value.TryGetProperty("entity-id", out var entityIdElement)
            || entityIdElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entityIdElement.GetString(), out var entityGuid))
        {
            return null;
        }

        return new EntityId(entityGuid);
    }

    private async Task<Dictionary<EntityId, EntitySnapshot>> GetCurrentSnapshotsByIdAsync(
        IReadOnlyCollection<EntityId> entityIds,
        CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
        {
            return new Dictionary<EntityId, EntitySnapshot>();
        }

        var getResult = await this.UnderlyingDataAccessLayer.GetAsync(
            new GetRequest(
                entityIds.Select(static id => new GetEntityRequest(id, null, null, null)).ToArray(),
                null,
                new Timestamp?[] { null }),
            cancellationToken);

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .ToDictionary(static entity => entity.EntityId, static entity => entity);
    }

    private bool TryApplyJsonPatch(
        JsonElement currentData,
        JsonElement patchOperations,
        out JsonElement patchedData,
        out string? error)
    {
        JsonPatch? patch;
        try
        {
            patch = JsonSerializer.Deserialize<JsonPatch>(patchOperations.GetRawText());
        }
        catch (JsonException exception)
        {
            patchedData = default;
            error = $"JsonPatch data is invalid JSON: {exception.Message}";
            return false;
        }

        if (patch is null)
        {
            patchedData = default;
            error = "JsonPatch data could not be deserialized.";
            return false;
        }

        var currentNode = JsonNode.Parse(currentData.GetRawText());
        if (currentNode is null)
        {
            patchedData = default;
            error = "JsonPatch target data is invalid.";
            return false;
        }

        var patchResult = patch.Apply(currentNode);
        if (!patchResult.IsSuccess)
        {
            patchedData = default;
            error = patchResult.Error;
            return false;
        }

        patchedData = this.ToJsonElement(patchResult.Result);
        error = null;
        return true;
    }

    private JsonElement ToJsonElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private sealed class CoalescedEntityState
    {
        public CoalescedEntityState(
            EntityId entityId,
            EntitySnapshot? currentSnapshot,
            JsonElement? workingData)
        {
            this.EntityId = entityId;
            this.CurrentSnapshot = currentSnapshot;
            this.WorkingData = workingData?.Clone();
            this.ExistsInStore = currentSnapshot is not null;
            this.CurrentTag = currentSnapshot?.ConcurrencyTag;
        }

        public EntityId EntityId { get; }

        public EntitySnapshot? CurrentSnapshot { get; }

        public JsonElement? WorkingData { get; set; }

        public bool ExistsInStore { get; }

        public ConcurrencyTag? CurrentTag { get; }

        public ConcurrencyTag? RequestedTag { get; set; }
    }
}
