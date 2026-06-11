using System.Text.Json;

namespace Phantom.Workspaces.Data.Serialization;

public static class EntityJsonSerializer
{
    public static bool TryDeserialize<TValue>(JsonElement element, out TValue? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<TValue>(element.GetRawText());
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
