using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

internal static class WorkspaceToolEntityUtilities
{
    public static async Task<EntitySnapshot?> TryGetEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName,
        CancellationToken cancellationToken)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            },
            cancellationToken);

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault();
    }

    public static async Task<EntitySnapshot> UpsertEntityByPrimaryNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName primaryName,
        JsonObject entityData,
        string updateComment,
        CancellationToken cancellationToken)
    {
        var currentEntity = await TryGetEntityByNameAsync(dataAccessLayer, primaryName, cancellationToken);
        var resultingEntityId = currentEntity?.EntityId ?? CreateDeterministicEntityId(primaryName, "workspace-tool-entity");

        entityData["entity-id"] = resultingEntityId.ToString();
        using var entityDataDocument = JsonDocument.Parse(entityData.ToJsonString());

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = updateComment,
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = resultingEntityId,
                        ConcurrencyTag = currentEntity?.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = entityDataDocument.RootElement.Clone(),
                    },
                ],
            },
            cancellationToken);

        var entityResult = AssertSingleEntityUpdate(updateResult, resultingEntityId);
        return entityResult.CurrentEntity ?? throw new InvalidOperationException($"Entity {resultingEntityId} update did not return a snapshot.");
    }

    public static EntityId CreateDeterministicEntityId(
        EntityName entityName,
        string stableNamespace)
    {
        var input = $"{stableNamespace}|{string.Join("|", entityName.Components)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new EntityId(new Guid(bytes));
    }

    private static EntityUpdateResult AssertSingleEntityUpdate(
        UpdateResult updateResult,
        EntityId entityId)
    {
        var entityResult = updateResult.EntityResults.Single(result => result.RequestedEntityId == entityId);
        if (entityResult.UpdateState == UpdateState.Failed || entityResult.Errors.Count > 0)
        {
            var errorMessages = string.Join("; ", entityResult.Errors.Select(static error => error.Message));
            throw new InvalidOperationException($"Entity update failed for {entityId}: {errorMessages}");
        }

        return entityResult;
    }
}
