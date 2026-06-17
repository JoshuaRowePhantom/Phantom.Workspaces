using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Adds or removes an interest on an entity: removes the existing interest relationship of the given
/// type targeting the entity, or creates one (targeting the entity, by the current session user) when
/// none exists. Backs the toggleable interest badges on entity cards.
/// </summary>
public static class InterestToggle
{
    public static async Task ToggleAsync(
        EntityBroker entityBroker,
        EntitySnapshot entity,
        string interestTypeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityBroker);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(interestTypeName);

        var existing = entity.Relationships.FirstOrDefault(
            relationship => relationship.Data is JsonElement data
                && IsInterestTargeting(data, entity.EntityId, interestTypeName));

        if (existing is not null)
        {
            var removeResult = await entityBroker.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = $"Clear {interestTypeName} interest" } },
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = existing.EntityId,
                            ConcurrencyTag = existing.ConcurrencyTag,
                            Data = null,
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                    ],
                },
                cancellationToken);
            ThrowIfFailed(removeResult);
            return;
        }

        var userId = entityBroker.EntityRepository.WorkspaceEntitySession.UserEntityId;
        var relationshipId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["{{interestTypeName}}", "relationship"],
              "names": [["relationships", "{{interestTypeName}}-{{entity.EntityId.Value}}"]],
              "participants": { "target": "{{entity.EntityId.Value}}", "user": "{{userId.Value}}" },
              "note": "Toggled from the entity badge."
            }
            """);
        var addResult = await entityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = $"Mark {interestTypeName} interest" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(relationshipId),
                        ConcurrencyTag = null,
                        Data = document.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            cancellationToken);
        ThrowIfFailed(addResult);
    }

    private static void ThrowIfFailed(UpdateResult updateResult)
    {
        var failure = updateResult.EntityResults.FirstOrDefault(static result => result.UpdateState == UpdateState.Failed);
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Failed to toggle interest: " + string.Join("; ", failure.Errors.Select(static error => error.Message)));
        }
    }

    private static bool IsInterestTargeting(JsonElement relationshipData, EntityId entityId, string interestTypeName)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object
            || !relationshipData.TryGetProperty("entity-types", out var entityTypes)
            || entityTypes.ValueKind != JsonValueKind.Array
            || !relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object
            || !participants.TryGetProperty("target", out var target)
            || target.ValueKind != JsonValueKind.String
            || !string.Equals(target.GetString(), entityId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return entityTypes.EnumerateArray().Any(
            entityType => entityType.ValueKind == JsonValueKind.String
                && string.Equals(entityType.GetString(), interestTypeName, StringComparison.Ordinal));
    }
}
