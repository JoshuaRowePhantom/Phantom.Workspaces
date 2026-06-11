using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data;

public sealed class EntityNameJsonConverter : JsonConverter<EntityName>
{
    public override EntityName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("EntityName must be a JSON array of strings.");
        }

        var components = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return components.Count == 0 ? EntityName.Root : new EntityName(components.ToArray());
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("EntityName components must be strings.");
            }

            var component = reader.GetString();
            if (string.IsNullOrWhiteSpace(component))
            {
                throw new JsonException("EntityName components cannot be null or whitespace.");
            }

            components.Add(component);
        }

        throw new JsonException("Unexpected end of JSON while reading EntityName.");
    }

    public override void Write(Utf8JsonWriter writer, EntityName value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var component in value.Components)
        {
            writer.WriteStringValue(component);
        }

        writer.WriteEndArray();
    }
}
