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
        EntitySnapshot entity)
    {
        var entityTypeNames = ReadEntityTypes(entity.Data ?? JsonDocument.Parse("{}").RootElement).ToHashSet();
        var interestTypeNames = interestCatalog.InterestTypeNames;
        var appliedNotesByType = GetAppliedInterests(entity, interestTypeNames);

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
        // If interest type specifies display-entity-types (not null/empty), filter by that
        if (interestType.DisplayEntityTypes.Count > 0)
        {
            if (!entityTypeNames.Any(typeName => interestType.DisplayEntityTypes.Contains(typeName)))
            {
                return false;
            }
        }

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

    private static Dictionary<string, string?> GetAppliedInterests(EntitySnapshot entity, IReadOnlySet<string> interestTypeNames)
    {
        var appliedNotesByType = new Dictionary<string, string?>(System.StringComparer.Ordinal);
        foreach (var relationship in entity.Relationships)
        {
            if (relationship.Data is not { } relationshipData
                || !IsTargetOf(relationshipData, entity.EntityId))
            {
                continue;
            }

            var note = ReadNote(relationshipData);
            foreach (var typeName in ReadEntityTypes(relationshipData))
            {
                if (interestTypeNames.Contains(typeName))
                {
                    appliedNotesByType[typeName] = note;
                }
            }
        }

        return appliedNotesByType;
    }

    private static bool IsTargetOf(JsonElement relationshipData, EntityId entityId)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object
            || !relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object
            || !participants.TryGetProperty("target", out var target)
            || target.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return string.Equals(target.GetString(), entityId.ToString(), System.StringComparison.OrdinalIgnoreCase);
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
