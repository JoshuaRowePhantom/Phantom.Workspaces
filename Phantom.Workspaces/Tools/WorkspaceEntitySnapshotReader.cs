using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

internal static class WorkspaceEntitySnapshotReader
{
    public static IReadOnlyCollection<string> GetEntityTypes(EntitySnapshot snapshot)
    {
        if (snapshot.Data is not JsonElement entityData
            || !entityData.TryGetProperty("entity-types", out var entityTypesElement)
            || entityTypesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return entityTypesElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    public static IReadOnlyCollection<EntityName> GetEntityNames(EntitySnapshot snapshot)
    {
        if (snapshot.Data is not JsonElement entityData
            || !entityData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<EntityName>();
        }

        return namesElement
            .EnumerateArray()
            .Where(static nameArray => nameArray.ValueKind == JsonValueKind.Array)
            .Select(static nameArray => nameArray
                .EnumerateArray()
                .Where(static part => part.ValueKind == JsonValueKind.String)
                .Select(static part => part.GetString())
                .Where(static part => !string.IsNullOrWhiteSpace(part))
                .Cast<string>()
                .ToArray())
            .Where(static parts => parts.Length > 0)
            .Select(static parts => new EntityName(parts))
            .ToArray();
    }

    public static string? TryGetStringProperty(
        EntitySnapshot snapshot,
        string propertyName)
    {
        if (snapshot.Data is JsonElement entityData
            && entityData.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind == JsonValueKind.String)
        {
            return valueElement.GetString();
        }

        return null;
    }

    public static IReadOnlyList<string>? TryGetStringArrayProperty(
        EntitySnapshot snapshot,
        string propertyName)
    {
        if (snapshot.Data is not JsonElement entityData
            || !entityData.TryGetProperty(propertyName, out var valueElement)
            || valueElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return valueElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }
}
