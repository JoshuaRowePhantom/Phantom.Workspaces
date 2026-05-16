using System.Reflection;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Resolves embedded built-in JSON schema documents.
/// </summary>
public sealed class BuiltinSchemaResolver
{
    private readonly IReadOnlyDictionary<string, JsonObject> schemasById;

    public BuiltinSchemaResolver()
    {
        this.schemasById = this.LoadSchemas(Assembly.GetExecutingAssembly());
    }

    public IReadOnlyCollection<string> SchemaIds => this.schemasById.Keys.ToArray();

    public bool TryGetSchema(
        string schemaId,
        out JsonObject schema)
    {
        return this.schemasById.TryGetValue(schemaId, out schema!);
    }

    public JsonObject GetSchema(
        string schemaId)
    {
        return this.schemasById[schemaId];
    }

    private IReadOnlyDictionary<string, JsonObject> LoadSchemas(
        Assembly assembly)
    {
        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(".JsonSchemas.", StringComparison.Ordinal)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            if (JsonNode.Parse(text) is not JsonObject schemaObject)
            {
                continue;
            }

            if (schemaObject.TryGetPropertyValue("$id", out var idNode)
                && idNode is JsonValue idValue
                && idValue.TryGetValue<string>(out var schemaId)
                && !string.IsNullOrWhiteSpace(schemaId))
            {
                schemas[schemaId] = (JsonObject)schemaObject.DeepClone();
            }
        }

        return schemas;
    }
}
