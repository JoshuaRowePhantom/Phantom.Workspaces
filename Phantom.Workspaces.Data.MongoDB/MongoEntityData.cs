using System;
using System.Buffers;
using System.Text.Json;
using MongoDB.Bson;

namespace Phantom.Workspaces.Data.MongoDB;

/// <summary>
/// Converts entity data between <see cref="JsonElement"/> and native BSON for storage and querying.
/// </summary>
/// <remarks>
/// Entity data is stored as native BSON (not an opaque JSON string) so the denormalized current
/// projection is natively queryable on arbitrary fields and participants. Object keys are stored
/// literally - MongoDB 5.0+ supports field names containing dots (<c>.</c>) and dollar signs
/// (<c>$</c>), so JSON Schema content with <c>$ref</c>/<c>$id</c>/<c>$defs</c> keys round-trips
/// faithfully (see https://www.mongodb.com/docs/manual/core/dot-dollar-considerations/). The
/// conversion is done manually rather than via <see cref="BsonDocument.Parse"/> / <c>ToJson</c> so
/// that <c>$</c>-prefixed keys are never misinterpreted as MongoDB extended-JSON or DBRef
/// constructs, and so numbers/strings round-trip as plain JSON.
/// </remarks>
internal static class MongoEntityData
{
    /// <summary>Converts a JSON element to a BSON document.</summary>
    public static BsonDocument ToBsonDocument(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Entity data must be a JSON object.", nameof(element));
        }

        return ToBsonValue(element).AsBsonDocument;
    }

    /// <summary>Converts a BSON document back to a JSON element.</summary>
    public static JsonElement ToJsonElement(BsonValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteJson(value, writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static BsonValue ToBsonValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var document = new BsonDocument();
                foreach (var property in element.EnumerateObject())
                {
                    document[property.Name] = ToBsonValue(property.Value);
                }

                return document;

            case JsonValueKind.Array:
                var array = new BsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ToBsonValue(item));
                }

                return array;

            case JsonValueKind.String:
                return new BsonString(element.GetString());

            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E') && element.TryGetInt64(out var longValue))
                {
                    return new BsonInt64(longValue);
                }

                return new BsonDouble(element.GetDouble());

            case JsonValueKind.True:
                return BsonBoolean.True;

            case JsonValueKind.False:
                return BsonBoolean.False;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return BsonNull.Value;
        }
    }

    private static void WriteJson(BsonValue value, Utf8JsonWriter writer)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                writer.WriteStartObject();
                foreach (var element in value.AsBsonDocument)
                {
                    writer.WritePropertyName(element.Name);
                    WriteJson(element.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case BsonType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsBsonArray)
                {
                    WriteJson(item, writer);
                }

                writer.WriteEndArray();
                break;

            case BsonType.String:
                writer.WriteStringValue(value.AsString);
                break;

            case BsonType.Int32:
                writer.WriteNumberValue(value.AsInt32);
                break;

            case BsonType.Int64:
                writer.WriteNumberValue(value.AsInt64);
                break;

            case BsonType.Double:
                writer.WriteNumberValue(value.AsDouble);
                break;

            case BsonType.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                break;

            case BsonType.Null:
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
