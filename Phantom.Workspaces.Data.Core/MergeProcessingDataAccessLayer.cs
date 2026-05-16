using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Processes JSON Patch update operations on top of an underlying data access layer.
/// The underlying data access layer receives only full-document replace operations.
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

        var patchChanges = request.Changes
            .Where(static change => change.EntityChangeMode == EntityChangeMode.JsonPatch)
            .ToArray();
        if (patchChanges.Length == 0)
        {
            return await this.UnderlyingDataAccessLayer.UpdateAsync(
                new UpdateRequest(
                    request.UpdateMetadata,
                    request.Changes.Select(static change => change with { EntityChangeMode = EntityChangeMode.Replace }).ToArray()),
                cancellationToken);
        }

        var failures = new List<EntityUpdateResult>();
        var patchEntityIds = new HashSet<EntityId>();
        foreach (var change in patchChanges)
        {
            if (change.EntityId is null)
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

            patchEntityIds.Add(change.EntityId.Value);
        }

        if (failures.Count > 0)
        {
            return new UpdateResult(failures);
        }

        var currentSnapshotsById = await this.GetCurrentSnapshotsByIdAsync(patchEntityIds, cancellationToken);
        var processedChanges = new List<EntityChange>(request.Changes.Count);

        foreach (var change in request.Changes)
        {
            if (change.EntityChangeMode != EntityChangeMode.JsonPatch)
            {
                processedChanges.Add(change with { EntityChangeMode = EntityChangeMode.Replace });
                continue;
            }

            var entityId = change.EntityId!.Value;
            currentSnapshotsById.TryGetValue(entityId, out var currentSnapshot);
            if (currentSnapshot is null || currentSnapshot.Data is null)
            {
                failures.Add(
                    new EntityUpdateResult(
                        UpdateState.Failed,
                        entityId,
                        entityId,
                        null,
                        ConcurrencyMatchState.NotMatched,
                        null,
                        new[] { new UpdateError("JsonPatch target entity does not exist.", entityId) }));
                continue;
            }

            if (change.ConcurrencyTag is null)
            {
                failures.Add(
                    new EntityUpdateResult(
                        UpdateState.Failed,
                        entityId,
                        entityId,
                        currentSnapshot.ConcurrencyTag,
                        ConcurrencyMatchState.NotMatched,
                        currentSnapshot,
                        new[] { new UpdateError("Concurrency tag is required.", entityId) }));
                continue;
            }

            if (currentSnapshot.ConcurrencyTag != change.ConcurrencyTag.Value)
            {
                failures.Add(
                    new EntityUpdateResult(
                        UpdateState.Failed,
                        entityId,
                        entityId,
                        currentSnapshot.ConcurrencyTag,
                        ConcurrencyMatchState.NotMatched,
                        currentSnapshot,
                        new[] { new UpdateError("Concurrency tag does not match.", entityId) }));
                continue;
            }

            if (change.Data is null || change.Data.Value.ValueKind != JsonValueKind.Array)
            {
                failures.Add(
                    new EntityUpdateResult(
                        UpdateState.Failed,
                        entityId,
                        entityId,
                        currentSnapshot.ConcurrencyTag,
                        ConcurrencyMatchState.NotMatched,
                        currentSnapshot,
                        new[] { new UpdateError("JsonPatch data must be an array of operations.", entityId) }));
                continue;
            }

            if (!this.TryApplyJsonPatch(currentSnapshot.Data.Value, change.Data.Value, out var patchedData, out var patchError))
            {
                failures.Add(
                    new EntityUpdateResult(
                        UpdateState.Failed,
                        entityId,
                        entityId,
                        currentSnapshot.ConcurrencyTag,
                        ConcurrencyMatchState.NotMatched,
                        currentSnapshot,
                        new[] { new UpdateError(patchError ?? "JsonPatch failed.", entityId) }));
                continue;
            }

            processedChanges.Add(
                new EntityChange(
                    entityId,
                    change.ConcurrencyTag,
                    patchedData,
                    EntityChangeMode.Replace));
        }

        if (failures.Count > 0)
        {
            return new UpdateResult(failures);
        }

        return await this.UnderlyingDataAccessLayer.UpdateAsync(
            new UpdateRequest(request.UpdateMetadata, processedChanges),
            cancellationToken);
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
                entityIds.ToArray(),
                null,
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
}
