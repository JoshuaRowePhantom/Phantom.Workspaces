using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Data;

public interface ISchemaAccessor
{
    Task<JsonElement?> ResolveSchemaByReferenceAsync(
        string schemaReference,
        CancellationToken cancellationToken = default);

    Task<SchemaRegistry> BuildSchemaRegistryAsync(
        CancellationToken cancellationToken = default);
}
