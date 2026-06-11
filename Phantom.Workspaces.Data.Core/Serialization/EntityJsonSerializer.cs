using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Phantom.Workspaces.Data.Serialization;

public static class EntityJsonSerializer
{
    public static TValue? Deserialize<TValue>(JsonElement element, JsonTypeInfo<TValue> jsonTypeInfo)
    {
        try
        {
            return element.Deserialize(jsonTypeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static bool TryDeserialize<TValue>(JsonElement element, JsonTypeInfo<TValue> jsonTypeInfo, out TValue? value)
    {
        value = Deserialize(element, jsonTypeInfo);
        return value is not null;
    }
}
