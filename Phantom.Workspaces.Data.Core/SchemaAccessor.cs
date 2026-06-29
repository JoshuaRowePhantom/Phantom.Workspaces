using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Data.Serialization;

namespace Phantom.Workspaces.Data;

public sealed class SchemaAccessor : ISchemaAccessor
{
    private const string SchemaEntityType = "json-schema";

    private readonly IDataAccessLayer dataAccessLayer;
    private readonly Dictionary<string, JsonElement> requestSchemasByName;
    private readonly ConcurrentDictionary<string, JsonElement?> schemasByReference = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private Dictionary<string, JsonElement>? schemaEntitiesById;
    private Dictionary<string, JsonElement>? schemasByEntityName;
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

    public SchemaAccessor(
        IDataAccessLayer dataAccessLayer,
        UpdateRequest? request,
        IReadOnlyDictionary<string, JsonElement> preloadedSchemaEntitiesById)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.requestSchemasByName = this.GetSchemasFromRequest(request);
        this.schemaEntitiesById = preloadedSchemaEntitiesById.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        this.schemasByEntityName = BuildSchemasByEntityName(this.schemaEntitiesById);
    }

    public IReadOnlyDictionary<string, JsonElement>? SchemaEntitiesById => this.schemaEntitiesById;

    public async Task<JsonElement?> ResolveSchemaByReferenceAsync(
        string schemaReference,
        CancellationToken cancellationToken = default)
    {
        if (this.schemasByReference.TryGetValue(schemaReference, out var cachedSchema))
        {
            return cachedSchema;
        }

        foreach (var schemaName in GetSchemaEntityNames(schemaReference))
        {
            if (this.requestSchemasByName.TryGetValue(schemaName, out var requestSchema))
            {
                this.schemasByReference[schemaReference] = requestSchema;
                return requestSchema;
            }

            if (this.schemasByEntityName?.TryGetValue(schemaName, out var preloadedEntity) == true)
            {
                this.schemasByReference[schemaReference] = preloadedEntity;
                return preloadedEntity;
            }

            if (!TryParseEntityName(schemaName, out var parsedSchemaName))
            {
                continue;
            }

            var getResult = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities = [new GetEntityRequest { EntityName = parsedSchemaName }],
                    Timestamps = new Timestamp?[] { null },
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var entity in getResult.Batches.SelectMany(static batch => batch.Entities))
            {
                if (entity.Data is { ValueKind: JsonValueKind.Object } storedSchema
                    && IsSchemaEntity(storedSchema))
                {
                    this.schemasByReference[schemaReference] = storedSchema;
                    return storedSchema;
                }
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
        await this.registryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.schemaRegistry is not null)
            {
                return this.schemaRegistry;
            }

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
                        Dialect = WorkspacesSchemaDialect.AllowingUnknownKeywords,
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
                        Dialect = WorkspacesSchemaDialect.AllowingUnknownKeywords,
                    },
                    new Uri(pair.Key, UriKind.Absolute));
            }

            this.schemaRegistry = schemaRegistry;
            return schemaRegistry;
        }
        finally
        {
            this.registryGate.Release();
        }
    }

    private async Task EnsureSchemasLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (this.schemaEntitiesById is not null)
        {
            return;
        }

        await this.loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.schemaEntitiesById is not null)
            {
                return;
            }

            var schemasById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            // Schema entities are tagged with the "json-schema" entity type, so the
            // registry can be built from a bounded type query instead of enumerating
            // the entire store.
            var queryResult = await this.dataAccessLayer.QueryAsync(
                new QueryRequest
                {
                    Clauses =
                    [
                        new TopLevelQueryClause
                        {
                            ClauseIdentifier = new QueryClauseIdentifier(SchemaEntityType),
                            Clause = new EntityTypeQueryClause
                            {
                                EntityTypeNames = new EntityTypeNameSet([SchemaEntityType]),
                            },
                        },
                    ],
                    Timestamps = new Timestamp?[] { null },
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var entityData in queryResult.Batches
                         .SelectMany(static batch => batch.Entities)
                         .Select(static entity => entity.Data)
                         .Where(static data => data is { ValueKind: JsonValueKind.Object })
                         .Select(static data => data!.Value))
            {
                if (TryGetSchemaPayloadId(entityData, out var schemaId)
                    && Uri.TryCreate(schemaId, UriKind.Absolute, out _))
                {
                    schemasById[schemaId] = entityData;
                }
            }

            this.schemaEntitiesById = schemasById;
            this.schemasByEntityName = BuildSchemasByEntityName(schemasById);
        }
        finally
        {
            this.loadGate.Release();
        }
    }

    private static bool TryParseEntityName(
        string name,
        out EntityName entityName)
    {
        if (name.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(name);
                var parsedEntityName = document.RootElement.TryReadEntityName();
                if (parsedEntityName is not null)
                {
                    entityName = parsedEntityName.Value;
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            entityName = default;
            return false;
        }

        entityName = new EntityName(name);
        return true;
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
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityObject);
        return schemaEntityDocument is not null
            && schemaEntityDocument.IsSchemaEntity();
    }

    private static IReadOnlyCollection<string> GetEntityNames(
        JsonElement entityObject)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityObject);
        return schemaEntityDocument?.GetCanonicalNames() ?? Array.Empty<string>();
    }

    private static Dictionary<string, JsonElement> BuildSchemasByEntityName(
        Dictionary<string, JsonElement> schemaEntitiesById)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var schemaEntity in schemaEntitiesById.Values)
        {
            foreach (var name in GetEntityNames(schemaEntity))
            {
                result.TryAdd(name, schemaEntity);
            }
        }

        return result;
    }

    private static HashSet<string> GetExplicitEntityTypeNames(
        JsonElement entityData)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityData);
        return schemaEntityDocument?.GetExplicitEntityTypeNames()
               ?? new HashSet<string>(StringComparer.Ordinal);
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
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(schemaEntity);
        if (schemaEntityDocument is null)
        {
            schemaId = string.Empty;
            return false;
        }

        return schemaEntityDocument.TryGetSchemaPayloadId(out schemaId);
    }

    public static string GetSchemaText(
        JsonElement schemaEntity)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(schemaEntity);
        if (schemaEntityDocument is not null
            && schemaEntityDocument.SchemaPayload is JsonElement schemaPayload)
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
                schemaPayload.WriteTo(payloadWriter);
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
            property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
