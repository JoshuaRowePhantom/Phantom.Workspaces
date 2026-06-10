using System.Reflection;
using Json.Schema;

namespace Phantom.Workspaces.Data.MongoDB;

public static class MongoDbConnectionDefinitionJsonSchema
{
    private const string ResourceName = "Phantom.Workspaces.Data.MongoDB.JsonSchemas.mongo-db-connection.json";

    public static JsonSchema Value { get; } = LoadFromEmbeddedResource();

    private static JsonSchema LoadFromEmbeddedResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded schema resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var schemaText = reader.ReadToEnd();
        return JsonSchema.FromText(schemaText);
    }
}
