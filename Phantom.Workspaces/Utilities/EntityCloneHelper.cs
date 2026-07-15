using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Utilities;

public static class EntityCloneHelper
{
    public static JsonElement RewriteEntityId(JsonElement entityData, EntityId newEntityId)
    {
        if (entityData.ValueKind != JsonValueKind.Object)
        {
            return entityData;
        }

        var node = JsonNode.Parse(entityData.GetRawText());
        if (node is JsonObject obj)
        {
            obj["entity-id"] = JsonValue.Create(newEntityId.ToString());
        }

        using var doc = JsonDocument.Parse(node!.ToJsonString());
        return doc.RootElement.Clone();
    }

    public static JsonElement RewriteRelationshipParticipantIds(
        JsonElement relationshipData,
        EntityId sourceId,
        EntityId cloneId)
    {
        if (relationshipData.ValueKind != JsonValueKind.Object)
        {
            return relationshipData;
        }

        var node = JsonNode.Parse(relationshipData.GetRawText());
        if (node is JsonObject obj && obj["participants"] is JsonNode participantsNode)
        {
            RewriteIdsInNode(participantsNode, sourceId.ToString(), cloneId.ToString());
        }

        using var doc = JsonDocument.Parse(node!.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void RewriteIdsInNode(JsonNode node, string sourceId, string cloneId)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var child = obj[key];
                if (child is JsonValue val && val.TryGetValue<string>(out var str) && str == sourceId)
                {
                    obj[key] = JsonValue.Create(cloneId);
                }
                else if (child is not null)
                {
                    RewriteIdsInNode(child, sourceId, cloneId);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                if (child is JsonValue val && val.TryGetValue<string>(out var str) && str == sourceId)
                {
                    arr[i] = JsonValue.Create(cloneId);
                }
                else if (child is not null)
                {
                    RewriteIdsInNode(child, sourceId, cloneId);
                }
            }
        }
    }
}
