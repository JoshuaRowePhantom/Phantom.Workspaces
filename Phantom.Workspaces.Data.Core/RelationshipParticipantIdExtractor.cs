using System.Text.Json;

namespace Phantom.Workspaces.Data;

public static class RelationshipParticipantIdExtractor
{
    public static bool TryGetRelationshipParticipantIds(
        JsonElement relationshipData,
        out IReadOnlyCollection<EntityId> participantIds)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object
            || !relationshipData.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Object)
        {
            participantIds = Array.Empty<EntityId>();
            return false;
        }

        var ids = new List<EntityId>();
        CollectEntityIds(participants, ids);
        participantIds = ids.Distinct().ToArray();
        return participantIds.Count > 0;
    }

    private static void CollectEntityIds(
        JsonElement value,
        ICollection<EntityId> ids)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                CollectEntityIds(property.Value, ids);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectEntityIds(item, ids);
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
}
