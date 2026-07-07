using System.Text.Json;
using MongoDB.Bson;

namespace Phantom.Workspaces.Data.Web.Client;

internal static class PersistedAgentJsonExtensions
{
    public static BsonDocument ToBsonDocument(this JsonElement jsonElement)
    {
        return BsonDocument.Parse(jsonElement.GetRawText());
    }

    public static BsonDocument? ToBsonDocument(this JsonElement? jsonElement)
    {
        if (jsonElement is null)
        {
            return null;
        }

        return BsonDocument.Parse(jsonElement.Value.GetRawText());
    }

    public static JsonElement? ToJsonElement(this BsonDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        using var jsonDocument = JsonDocument.Parse(document.ToJson());
        return jsonDocument.RootElement.Clone();
    }
}
