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

    public static SchemaEntityDocument? Deserialize(JsonElement entityData)
    {
        if (entityData.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return EntityJsonSerializer.Deserialize(entityData, EntitySerializationJsonContext.Default.SchemaEntityDocument);
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
            && EntityJsonSerializer.TryDeserialize(
                schemaPayloadElement,
                EntitySerializationJsonContext.Default.SchemaPayloadDocument,
                out SchemaPayloadDocument? schemaPayloadDocument)
            && schemaPayloadDocument is not null
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
