using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Projects an entity's interests into toggleable badge glyphs: for each interest type in the catalog,
/// a <see cref="BadgeModel"/> showing the applied or not-applied glyph/description. An interest is
/// applied when the entity is the <c>target</c> of a relationship of that interest type (the relationships
/// are loaded onto the entity snapshot); the applied badge's tooltip includes the relationship's
/// reason <c>note</c> when present.
/// Badges are filtered based on display-entity-types (from interest-type) and display-interest-types (from entity-type).
/// </summary>
public static class InterestBadgeProjector
{
    public static IReadOnlyList<BadgeModel> Project(
        InterestCatalog interestCatalog, 
        EntityTypeCatalog entityTypeCatalog,
        EntitySnapshot entity,
        EntityId userId,
        EntityId userComputerProfileId)
    {
        var entityTypeNames = ReadEntityTypes(entity.Data ?? JsonDocument.Parse("{}").RootElement).ToHashSet();
        var appliedNotesByType = GetAppliedInterests(entity, interestCatalog.InterestTypes, userId, userComputerProfileId);

        return interestCatalog.InterestTypes
            .Where(interestType => ShouldShowBadge(interestType, entityTypeNames, entityTypeCatalog))
            .Select(interestType =>
            {
                var applied = appliedNotesByType.TryGetValue(interestType.Name, out var note);
                return new BadgeModel(
                    interestType.Name,
                    applied ? interestType.AppliedGlyph : interestType.NotAppliedGlyph,
                    BuildTooltip(interestType, applied, note),
                    applied);
            })
            .ToList();
    }

    private static bool ShouldShowBadge(
        InterestTypeDefinition interestType,
        IReadOnlySet<string> entityTypeNames,
        EntityTypeCatalog entityTypeCatalog)
    {
        // Handle display-entity-types from interest type definition
        if (interestType.DisplayEntityTypes is { } displayEntityTypes)
        {
            if (displayEntityTypes.Count > 0)
            {
                // Non-empty: only show on listed entity types
                if (!entityTypeNames.Any(typeName => displayEntityTypes.Contains(typeName)))
                {
                    return false;
                }
            }
            else
            {
                // Empty array: only show if entity type explicitly requests it
                bool isRequestedByEntityType = false;
                foreach (var entityTypeName in entityTypeNames)
                {
                    var entityTypeDefinition = entityTypeCatalog.EntityTypes
                        .FirstOrDefault(et => et.Name == entityTypeName);

                    if (entityTypeDefinition?.DisplayInterestTypes.Contains(interestType.Name) == true)
                    {
                        isRequestedByEntityType = true;
                        break;
                    }
                }

                if (!isRequestedByEntityType)
                {
                    return false;
                }
            }
        }
        // If null: no filtering from interest type side, continue to check entity type preferences

        // If any entity type specifies display-interest-types, filter by that
        foreach (var entityTypeName in entityTypeNames)
        {
            var entityTypeDefinition = entityTypeCatalog.EntityTypes
                .FirstOrDefault(et => et.Name == entityTypeName);

            if (entityTypeDefinition?.DisplayInterestTypes.Count > 0)
            {
                if (!entityTypeDefinition.DisplayInterestTypes.Contains(interestType.Name))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string BuildTooltip(InterestTypeDefinition interestType, bool applied, string? note)
    {
        var primary = applied
            ? FirstNonEmpty(interestType.AppliedDescription, interestType.AppliedActionText, interestType.Name)
            : FirstNonEmpty(interestType.NotAppliedActionText, interestType.NotAppliedDescription, interestType.Name);

        return applied && !string.IsNullOrWhiteSpace(note)
            ? $"{primary}\n{note}"
            : primary;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static Dictionary<string, string?> GetAppliedInterests(
        EntitySnapshot entity,
        IReadOnlyList<InterestTypeDefinition> interestTypes,
        EntityId userId,
        EntityId userComputerProfileId)
    {
        var appliedNotesByType = new Dictionary<string, string?>(System.StringComparer.Ordinal);
        foreach (var relationship in entity.Relationships)
        {
            if (relationship.Data is not { } relationshipData)
            {
                continue;
            }

            var relationshipEntityTypes = ReadEntityTypes(relationshipData).ToHashSet();
            foreach (var interestType in interestTypes)
            {
                if (!relationshipEntityTypes.Contains(interestType.Name))
                {
                    continue;
                }

                if (!IsAppliedTo(relationshipData, entity.EntityId, interestType, userId, userComputerProfileId))
                {
                    continue;
                }

                appliedNotesByType[interestType.Name] = ReadNote(relationshipData);
            }
        }

        return appliedNotesByType;
    }

    private static bool IsAppliedTo(
        JsonElement relationshipData,
        EntityId entityId,
        InterestTypeDefinition interestType,
        EntityId userId,
        EntityId userComputerProfileId)
    {
        if (!TryReadParticipant(relationshipData, interestType.TargetParticipant, out var targetValue)
            || !string.Equals(targetValue, entityId.ToString(), System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var scope in interestType.AppliesTo)
        {
            var expected = scope.SessionValue == InterestSessionValue.UserComputerProfileEntityId
                ? userComputerProfileId.ToString()
                : userId.ToString();
            if (!TryReadParticipant(relationshipData, scope.ParticipantPropertyName, out var actual)
                || !string.Equals(actual, expected, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadParticipant(JsonElement relationshipData, string participantName, out string? value)
    {
        value = null;
        if (relationshipData.ValueKind != JsonValueKind.Object
            || !relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object
            || !participants.TryGetProperty(participantName, out var participantElement)
            || participantElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = participantElement.GetString();
        return value is not null;
    }

    private static string? ReadNote(JsonElement relationshipData)
        => relationshipData.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String
            ? note.GetString()
            : null;

    private static IEnumerable<string> ReadEntityTypes(JsonElement relationshipData)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object
            || !relationshipData.TryGetProperty("entity-types", out var entityTypes)
            || entityTypes.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entityType in entityTypes.EnumerateArray())
        {
            if (entityType.ValueKind == JsonValueKind.String)
            {
                yield return entityType.GetString()!;
            }
        }
    }
}
