using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data.Serialization;

public sealed record SchemaEntityDocument
    : EntityDocumentBase
{
    [JsonPropertyName("schema")]
    public JsonElement? SchemaPayload { get; init; }

    [JsonPropertyName("$id")]
    public string? SchemaId { get; init; }

    public static bool TryParse(JsonElement entityData, out SchemaEntityDocument? schemaEntityDocument)
    {
        if (entityData.ValueKind != JsonValueKind.Object)
        {
            schemaEntityDocument = null;
            return false;
        }

        return EntityJsonSerializer.TryDeserialize(entityData, out schemaEntityDocument);
    }

    public bool IsSchemaEntity()
    {
        return this.SchemaPayload is not null
            || this.GetExplicitEntityTypeNames().Contains("json-schema")
            || !string.IsNullOrWhiteSpace(this.SchemaId);
    }

    public bool TryGetSchemaPayloadId(out string schemaPayloadId)
    {
        schemaPayloadId = string.Empty;
        if (this.SchemaPayload is JsonElement schemaPayloadElement
            && schemaPayloadElement.ValueKind == JsonValueKind.Object
            && EntityJsonSerializer.TryDeserialize(schemaPayloadElement, out SchemaPayloadDocument? schemaPayloadDocument)
            && !string.IsNullOrWhiteSpace(schemaPayloadDocument.SchemaId))
        {
            schemaPayloadId = schemaPayloadDocument.SchemaId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(this.SchemaId))
        {
            schemaPayloadId = this.SchemaId;
            return true;
        }

        return false;
    }
}

public sealed record SchemaPayloadDocument
{
    [JsonPropertyName("$id")]
    public string? SchemaId { get; init; }
}
