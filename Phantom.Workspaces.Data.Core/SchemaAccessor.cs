using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Data;

public sealed class SchemaAccessor : ISchemaAccessor
{
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly Dictionary<string, JsonElement> requestSchemasByName;
    private readonly Dictionary<string, JsonElement?> schemasByReference = new(StringComparer.Ordinal);
    private Dictionary<string, JsonElement>? schemaEntitiesByName;
    private Dictionary<string, JsonElement>? schemaEntitiesById;
    private SchemaRegistry? schemaRegistry;

    public SchemaAccessor(
        IDataAccessLayer dataAccessLayer,
        UpdateRequest? request = null)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.requestSchemasByName = this.GetSchemasFromRequest(request);
    }

    public SchemaAccessor(
        IDataAccessLayer dataAccessLayer,
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.requestSchemasByName = requestSchemasByName.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
    }

    public async Task<JsonElement?> ResolveSchemaByReferenceAsync(
        string schemaReference,
        CancellationToken cancellationToken = default)
    {
        if (this.schemasByReference.TryGetValue(schemaReference, out var cachedSchema))
        {
            return cachedSchema;
        }

        await this.EnsureSchemasLoadedAsync(cancellationToken).ConfigureAwait(false);
        foreach (var schemaName in GetSchemaEntityNames(schemaReference))
        {
            if (this.requestSchemasByName.TryGetValue(schemaName, out var requestSchema))
            {
                this.schemasByReference[schemaReference] = requestSchema;
                return requestSchema;
            }

            if (this.schemaEntitiesByName!.TryGetValue(schemaName, out var storedSchema))
            {
                this.schemasByReference[schemaReference] = storedSchema;
                return storedSchema;
            }
        }

        this.schemasByReference[schemaReference] = null;
        return null;
    }

    public async Task<SchemaRegistry> BuildSchemaRegistryAsync(
        CancellationToken cancellationToken = default)
    {
        if (this.schemaRegistry is not null)
        {
            return this.schemaRegistry;
        }

        await this.EnsureSchemasLoadedAsync(cancellationToken).ConfigureAwait(false);
        var schemaRegistry = new SchemaRegistry();
        var requestSchemasById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var schemaEntity in this.requestSchemasByName.Values)
        {
            if (!TryGetSchemaPayloadId(schemaEntity, out var schemaId)
                || !Uri.TryCreate(schemaId, UriKind.Absolute, out _))
            {
                continue;
            }

            requestSchemasById[schemaId] = schemaEntity;
        }

        foreach (var pair in requestSchemasById)
        {
            if (!Uri.TryCreate(pair.Key, UriKind.Absolute, out var schemaUri))
            {
                continue;
            }

            _ = JsonSchema.FromText(
                GetSchemaText(pair.Value),
                new BuildOptions
                {
                    SchemaRegistry = schemaRegistry,
                },
                schemaUri);
        }

        foreach (var pair in this.schemaEntitiesById!)
        {
            if (requestSchemasById.ContainsKey(pair.Key))
            {
                continue;
            }

            _ = JsonSchema.FromText(
                GetSchemaText(pair.Value),
                new BuildOptions
                {
                    SchemaRegistry = schemaRegistry,
                },
                new Uri(pair.Key, UriKind.Absolute));
        }

        this.schemaRegistry = schemaRegistry;
        return schemaRegistry;
    }

    private async Task EnsureSchemasLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (this.schemaEntitiesByName is not null && this.schemaEntitiesById is not null)
        {
            return;
        }

        var schemasByName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var schemasById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
#pragma warning disable CS0618
        var exportResult = await this.dataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618

        foreach (var entityData in exportResult.ChangeBatches
                     .SelectMany(static batch => batch.Entities)
                     .Select(static entity => entity.Data)
                     .Where(static data => data is { ValueKind: JsonValueKind.Object })
                     .Select(static data => data!.Value))
        {
            if (!IsSchemaEntity(entityData))
            {
                continue;
            }

            foreach (var name in GetEntityNames(entityData))
            {
                schemasByName[name] = entityData;
            }

            if (TryGetSchemaPayloadId(entityData, out var schemaId)
                && Uri.TryCreate(schemaId, UriKind.Absolute, out _))
            {
                schemasById[schemaId] = entityData;
            }
        }

        this.schemaEntitiesByName = schemasByName;
        this.schemaEntitiesById = schemasById;
    }

    private Dictionary<string, JsonElement> GetSchemasFromRequest(
        UpdateRequest? request)
    {
        var schemasByName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request is null)
        {
            return schemasByName;
        }

        foreach (var change in request.Changes)
        {
            if (change.Data is not { } data || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!IsSchemaEntity(data))
            {
                continue;
            }

            foreach (var name in GetEntityNames(data))
            {
                schemasByName[name] = data;
            }
        }

        return schemasByName;
    }

    private static bool IsSchemaEntity(
        JsonElement entityObject)
    {
        return entityObject.TryGetProperty("schema", out _)
            || GetExplicitEntityTypeNames(entityObject).Contains("json-schema")
            || entityObject.TryGetProperty("$id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String;
    }

    private static IReadOnlyCollection<string> GetEntityNames(
        JsonElement entityObject)
    {
        if (!entityObject.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var nameElement in namesElement.EnumerateArray())
        {
            if (nameElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = nameElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (components.Length > 0)
            {
                names.Add(JsonSerializer.Serialize(components));
            }
        }

        return names;
    }

    private static HashSet<string> GetExplicitEntityTypeNames(
        JsonElement entityData)
    {
        if (!entityData.TryGetProperty("entity-types", out var typeNames)
            || typeNames.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return typeNames.EnumerateArray()
            .Where(static typeName => typeName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(typeName.GetString()))
            .Select(static typeName => typeName.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyCollection<string> GetSchemaEntityNames(
        string schemaReference)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(schemaReference))
        {
            return names;
        }

        if (schemaReference.StartsWith("[", StringComparison.Ordinal))
        {
            names.Add(schemaReference);
        }
        else
        {
            names.Add(JsonSerializer.Serialize(new[] { "json-schemas", schemaReference }));
        }

        if (!names.Contains(schemaReference, StringComparer.Ordinal))
        {
            names.Add(schemaReference);
        }

        return names;
    }

    public static bool TryGetSchemaPayloadId(
        JsonElement schemaEntity,
        out string schemaId)
    {
        schemaId = string.Empty;
        if (schemaEntity.TryGetProperty("schema", out var schemaPayload)
            && schemaPayload.ValueKind == JsonValueKind.Object
            && schemaPayload.TryGetProperty("$id", out var payloadId)
            && payloadId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(payloadId.GetString()))
        {
            schemaId = payloadId.GetString()!;
            return true;
        }

        if (schemaEntity.TryGetProperty("$id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            schemaId = idElement.GetString()!;
            return true;
        }

        return false;
    }

    public static string GetSchemaText(
        JsonElement schemaEntity)
    {
        if (schemaEntity.TryGetProperty("schema", out var schemaPayload))
        {
            if (schemaPayload.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(schemaPayload.GetString()))
            {
                using var payloadStream = new MemoryStream();
                using var payloadWriter = new Utf8JsonWriter(payloadStream);
                payloadWriter.WriteStartObject();
                payloadWriter.WriteString("$ref", schemaPayload.GetString());
                payloadWriter.WriteEndObject();
                payloadWriter.Flush();
                return System.Text.Encoding.UTF8.GetString(payloadStream.ToArray());
            }

            if (schemaPayload.ValueKind == JsonValueKind.Object)
            {
                using var payloadStream = new MemoryStream();
                using var payloadWriter = new Utf8JsonWriter(payloadStream);
                WriteElementWithoutCustomKeywords(schemaPayload, payloadWriter);
                payloadWriter.Flush();
                return System.Text.Encoding.UTF8.GetString(payloadStream.ToArray());
            }
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (var property in schemaEntity.EnumerateObject())
        {
            if (property.Name.Equals("$id", StringComparison.Ordinal)
                || property.Name.Equals("entity-id", StringComparison.Ordinal)
                || property.Name.Equals("entity-types", StringComparison.Ordinal)
                || property.Name.Equals("names", StringComparison.Ordinal)
                || property.Name.Equals("unevaluatedProperties", StringComparison.Ordinal))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            WriteElementWithoutCustomKeywords(property.Value, writer);
        }

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElementWithoutCustomKeywords(
        JsonElement element,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "x-entity-type", StringComparison.Ordinal)
                        || string.Equals(property.Name, "x-default-mime-type", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteElementWithoutCustomKeywords(property.Value, writer);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithoutCustomKeywords(item, writer);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }
}
