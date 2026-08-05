using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Adds or removes an interest on an entity: removes the existing interest relationship of the given
/// type targeting the entity (matching the interest-type's declared <c>target-participant</c> and all
/// its <c>applies-to</c> session-scope participants), or creates one when none exists. Backs the
/// toggleable interest badges on entity cards. The participant shape is entirely data-driven from the
/// <see cref="InterestTypeDefinition"/>, so the same code serves every interest type (including the
/// <c>default</c> interest, whose relationship uses <c>value</c>/<c>applied-to</c> participants).
/// </summary>
public static class InterestToggle
{
    public static async Task ToggleAsync(
        EntityBroker entityBroker,
        EntitySnapshot entity,
        InterestTypeDefinition interest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityBroker);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(interest);

        var session = entityBroker.EntityRepository.WorkspaceEntitySession;
        EntityId ResolveSessionValue(InterestSessionValue value) =>
            value == InterestSessionValue.UserComputerProfileEntityId
                ? session.UserComputerProfileEntityId
                : session.UserEntityId;

        var existing = entity.Relationships.FirstOrDefault(
            relationship => relationship.Data is JsonElement data
                && IsInterestTargeting(data, entity.EntityId, interest, ResolveSessionValue));

        if (existing is not null)
        {
            var removeResult = await entityBroker.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = $"Clear {interest.Name} interest" } },
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

        var relationshipId = Guid.NewGuid();
        var participants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [interest.TargetParticipant] = entity.EntityId.Value.ToString(),
        };
        foreach (var scope in interest.AppliesTo)
        {
            participants[scope.ParticipantPropertyName] = ResolveSessionValue(scope.SessionValue).Value.ToString();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", relationshipId);
            writer.WriteStartArray("entity-types");
            writer.WriteStringValue("entity");
            writer.WriteStringValue(interest.Name);
            writer.WriteStringValue("relationship");
            writer.WriteEndArray();
            writer.WriteStartArray("names");
            writer.WriteStartArray();
            writer.WriteStringValue("relationships");
            writer.WriteStringValue($"{interest.Name}-{entity.EntityId.Value}");
            writer.WriteEndArray();
            writer.WriteEndArray();
            writer.WriteStartObject("participants");
            foreach (var (key, value) in participants)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
            writer.WriteString("note", "Toggled from the entity badge.");
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var addResult = await entityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = $"Mark {interest.Name} interest" } },
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

    private static bool IsInterestTargeting(
        JsonElement relationshipData,
        EntityId entityId,
        InterestTypeDefinition interest,
        Func<InterestSessionValue, EntityId> resolveSessionValue)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object
            || !relationshipData.TryGetProperty("entity-types", out var entityTypes)
            || entityTypes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var carriesInterestType = false;
        foreach (var entityType in entityTypes.EnumerateArray())
        {
            if (entityType.ValueKind == JsonValueKind.String
                && string.Equals(entityType.GetString(), interest.Name, StringComparison.Ordinal))
            {
                carriesInterestType = true;
                break;
            }
        }

        if (!carriesInterestType)
        {
            return false;
        }

        if (!TryReadParticipant(relationshipData, interest.TargetParticipant, out var targetValue)
            || !string.Equals(targetValue, entityId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var scope in interest.AppliesTo)
        {
            var expected = resolveSessionValue(scope.SessionValue).ToString();
            if (!TryReadParticipant(relationshipData, scope.ParticipantPropertyName, out var actual)
                || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadParticipant(JsonElement relationshipData, string participantName, out string? value)
    {
        value = null;
        if (!relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object
            || !participants.TryGetProperty(participantName, out var participantElement)
            || participantElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = participantElement.GetString();
        return value is not null;
    }
}
