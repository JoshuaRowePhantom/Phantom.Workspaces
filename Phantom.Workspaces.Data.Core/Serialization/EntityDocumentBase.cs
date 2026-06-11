using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data.Serialization;

public abstract record EntityDocumentBase
{
    [JsonPropertyName("entity-id")]
    public string? EntityId { get; init; }

    [JsonPropertyName("entity-types")]
    public string[]? EntityTypeNames { get; init; }

    [JsonPropertyName("names")]
    public string[][]? Names { get; init; }

    public IReadOnlyCollection<string> GetCanonicalNames()
    {
        if (this.Names is not { Length: > 0 })
        {
            return Array.Empty<string>();
        }

        var canonicalNames = new List<string>();
        foreach (var nameComponents in this.Names)
        {
            if (nameComponents is not { Length: > 0 } || nameComponents.Any(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var nameElement = JsonSerializer.SerializeToElement(nameComponents);
            var entityNameDocument = CoreEntityNameDocument.Deserialize(nameElement);
            if (entityNameDocument is not null)
            {
                canonicalNames.Add(entityNameDocument.ToCanonicalName());
            }
        }

        return canonicalNames;
    }

    public HashSet<string> GetExplicitEntityTypeNames()
    {
        return (this.EntityTypeNames ?? [])
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }
}
