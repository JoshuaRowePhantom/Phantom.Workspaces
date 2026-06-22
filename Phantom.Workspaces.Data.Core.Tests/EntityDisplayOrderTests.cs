using System.Reflection;
using System.Text.Json;

namespace Phantom.Workspaces.Data.Tests;

public sealed class EntityDisplayOrderTests
{
    private const string EntityTypeResourcePrefix = "Phantom.Workspaces.Data.JsonEntities.schema-definitions.";

    private static IEnumerable<(string ResourceName, JsonDocument Document)> LoadEntityTypeDefinitionDocuments()
    {
        var assembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(EntityTypeResourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith("-entity-type.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            yield return (resourceName, JsonDocument.Parse(reader.ReadToEnd()));
        }
    }

    private static bool IsEntityTypeDefinition(JsonElement root)
    {
        return root.TryGetProperty("entity-types", out var types)
            && types.ValueKind == JsonValueKind.Array
            && types.EnumerateArray().Any(static type =>
                type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "entity-type", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryEntityTypeDefinition_HasEntityDisplayOrder()
    {
        var missing = new List<string>();
        foreach (var (resourceName, document) in LoadEntityTypeDefinitionDocuments())
        {
            using (document)
            {
                if (!IsEntityTypeDefinition(document.RootElement))
                {
                    continue;
                }

                if (!document.RootElement.TryGetProperty("entity-display-order", out var order)
                    || order.ValueKind != JsonValueKind.Number)
                {
                    missing.Add(resourceName);
                }
            }
        }

        Assert.True(missing.Count == 0, $"Entity type definitions missing entity-display-order: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EntityDisplayOrderValues_AreUnique()
    {
        var valuesByOrder = new Dictionary<double, List<string>>();
        foreach (var (resourceName, document) in LoadEntityTypeDefinitionDocuments())
        {
            using (document)
            {
                if (IsEntityTypeDefinition(document.RootElement)
                    && document.RootElement.TryGetProperty("entity-display-order", out var order)
                    && order.ValueKind == JsonValueKind.Number)
                {
                    var value = order.GetDouble();
                    if (!valuesByOrder.TryGetValue(value, out var list))
                    {
                        list = [];
                        valuesByOrder[value] = list;
                    }

                    list.Add(resourceName);
                }
            }
        }

        var duplicates = valuesByOrder
            .Where(static pair => pair.Value.Count > 1)
            .Select(static pair => $"{pair.Key}: {string.Join(", ", pair.Value)}")
            .ToArray();

        Assert.True(duplicates.Length == 0, $"Duplicate entity-display-order values: {string.Join(" | ", duplicates)}");
    }
}
