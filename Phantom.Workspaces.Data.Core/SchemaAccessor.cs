using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Data.Serialization;

namespace Phantom.Workspaces.Data;

public sealed class SchemaAccessor : ISchemaAccessor
{
    private const string SchemaEntityType = "json-schema";

    // NJsonSchema's internal static Dictionary is not thread-safe; serialise all
    // JsonSchema.FromText calls process-wide to prevent concurrent corruption.
    private static readonly SemaphoreSlim buildGate = new(1, 1);

    private readonly IDataAccessLayer dataAccessLayer;
    private AsyncTaskCache<string, JsonElement?> schemasByReference = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private Dictionary<string, JsonElement>? schemaEntitiesById;
    private Dictionary<string, JsonElement>? schemasByEntityName;
    private SchemaRegistry? schemaRegistry;

    public SchemaAccessor(IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer;
    }

    public IReadOnlyDictionary<string, JsonElement>? SchemaEntitiesById => this.schemaEntitiesById;

    /// <summary>
    /// Creates a per-request overlay <see cref="ISchemaAccessor"/> that checks schemas from
    /// <paramref name="request"/> first and falls back to this singleton for everything else.
    /// Returns <c>this</c> when the request contains no schema changes.
    /// </summary>
    public ISchemaAccessor CreateOverlayForRequest(UpdateRequest request)
    {
        var requestSchemas = BuildRequestSchemas(request);
        if (requestSchemas.Count == 0)
        {
            return this;
        }

        return new PerRequestSchemaAccessor(this, requestSchemas);
    }

    public Task<JsonElement?> ResolveSchemaByReferenceAsync(
        string schemaReference,
        CancellationToken cancellationToken = default)
    {
        return this.schemasByReference.GetOrFetchAsync(
            schemaReference,
            this.FetchSchemaAsync,
            cancellationToken);
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

            var registry = await BuildRegistryFromSchemasAsync(
                this.schemaEntitiesById!,
                overrideSchemasById: null,
                cancellationToken).ConfigureAwait(false);
            this.schemaRegistry = registry;
            return registry;
        }
        finally
        {
            this.registryGate.Release();
        }
    }

    /// <summary>
    /// Clears all internal caches so the next operation re-fetches schemas from the DAL.
    /// Call this after a successful schema update to prevent stale schema data.
    /// </summary>
    public void InvalidateOnSchemaUpdate()
    {
        this.schemaEntitiesById = null;
        this.schemasByEntityName = null;
        this.schemaRegistry = null;
        this.schemasByReference = new AsyncTaskCache<string, JsonElement?>(StringComparer.Ordinal);
    }

    internal async Task EnsureSchemasLoadedAsync(CancellationToken cancellationToken)
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

    private async Task<JsonElement?> FetchSchemaAsync(
        string schemaReference,
        CancellationToken cancellationToken)
    {
        await this.EnsureSchemasLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (this.schemasByEntityName?.TryGetValue(schemaReference, out var preloadedEntity) == true)
        {
            return preloadedEntity;
        }

        // Entity-type schemas not in the preloaded index are stored under a two-component
        // name ["entity-types","<reference>"], so use that as the canonical lookup key.
        var entityName = new EntityName("entity-types", schemaReference);
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityName = entityName }],
                Timestamps = new Timestamp?[] { null },
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var entity in getResult.Batches.SelectMany(static batch => batch.Entities))
        {
            if (entity.Data is { ValueKind: JsonValueKind.Object } storedSchema
                && IsSchemaEntity(storedSchema))
            {
                return storedSchema;
            }
        }

        return null;
    }

    internal static async Task<SchemaRegistry> BuildRegistryFromSchemasAsync(
        IReadOnlyDictionary<string, JsonElement> baseSchemaEntitiesById,
        IReadOnlyDictionary<string, JsonElement>? overrideSchemasById,
        CancellationToken cancellationToken)
    {
        var registry = new SchemaRegistry();
        await buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (overrideSchemasById is not null)
            {
                foreach (var (id, schema) in overrideSchemasById)
                {
                    if (Uri.TryCreate(id, UriKind.Absolute, out var overrideUri))
                    {
                        _ = JsonSchema.FromText(
                            GetSchemaText(schema),
                            new BuildOptions
                            {
                                SchemaRegistry = registry,
                                Dialect = WorkspacesSchemaDialect.AllowingUnknownKeywords,
                            },
                            overrideUri);
                    }
                }
            }

            foreach (var (id, schema) in baseSchemaEntitiesById)
            {
                if (overrideSchemasById?.ContainsKey(id) == true)
                {
                    continue;
                }

                if (Uri.TryCreate(id, UriKind.Absolute, out var baseUri))
                {
                    _ = JsonSchema.FromText(
                        GetSchemaText(schema),
                        new BuildOptions
                        {
                            SchemaRegistry = registry,
                            Dialect = WorkspacesSchemaDialect.AllowingUnknownKeywords,
                        },
                        baseUri);
                }
            }
        }
        finally
        {
            buildGate.Release();
        }

        return registry;
    }

    internal static Dictionary<string, JsonElement> BuildSchemasByEntityName(
        Dictionary<string, JsonElement> schemaEntitiesById)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (schemaId, schemaEntity) in schemaEntitiesById)
        {
            result.TryAdd(schemaId, schemaEntity);
            foreach (var name in GetEntityDefinedTypeNames(schemaEntity))
            {
                result.TryAdd(name, schemaEntity);
            }
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, JsonElement> BuildRequestSchemas(UpdateRequest request)
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var change in request.Changes)
        {
            if (change.Data is not { ValueKind: JsonValueKind.Object } data
                || !IsSchemaEntity(data))
            {
                continue;
            }

            if (TryGetSchemaPayloadId(data, out var schemaId)
                && Uri.TryCreate(schemaId, UriKind.Absolute, out _))
            {
                schemas[schemaId] = data;
            }

            foreach (var name in GetEntityDefinedTypeNames(data))
            {
                schemas.TryAdd(name, data);
            }
        }

        return schemas;
    }

    /// <summary>
    /// Returns the entity-type names that this schema entity defines, extracted from
    /// <c>["entity-types","X"]</c> entries in the <c>names</c> property. Includes both
    /// the plain entity-type name (e.g., <c>agent-manifest</c>) and the canonical JSON
    /// array format (e.g., <c>["entity-types","agent-manifest"]</c>) so that callers
    /// using either convention can resolve schemas.
    /// </summary>
    private static IEnumerable<string> GetEntityDefinedTypeNames(JsonElement entityData)
    {
        var doc = SchemaEntityDocument.Deserialize(entityData);
        if (doc?.Names is not { Length: > 0 } names)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var n in names)
        {
            if (n is { Length: 2 }
                && string.Equals(n[0], "entity-types", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(n[1]))
            {
                result.Add(n[1]);
                result.Add(System.Text.Json.JsonSerializer.Serialize(n));
            }
        }

        return result;
    }

    private static bool IsSchemaEntity(JsonElement entityObject)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityObject);
        return schemaEntityDocument is not null && schemaEntityDocument.IsSchemaEntity();
    }

    public static bool TryGetSchemaPayloadId(JsonElement schemaEntity, out string schemaId)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(schemaEntity);
        if (schemaEntityDocument is null)
        {
            schemaId = string.Empty;
            return false;
        }

        return schemaEntityDocument.TryGetSchemaPayloadId(out schemaId);
    }

    public static string GetSchemaText(JsonElement schemaEntity)
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

    private sealed class PerRequestSchemaAccessor : ISchemaAccessor
    {
        private readonly SchemaAccessor _base;
        private readonly IReadOnlyDictionary<string, JsonElement> _requestSchemas;

        internal PerRequestSchemaAccessor(
            SchemaAccessor baseAccessor,
            IReadOnlyDictionary<string, JsonElement> requestSchemas)
        {
            _base = baseAccessor;
            _requestSchemas = requestSchemas;
        }

        public Task<JsonElement?> ResolveSchemaByReferenceAsync(
            string schemaReference,
            CancellationToken cancellationToken = default)
        {
            if (_requestSchemas.TryGetValue(schemaReference, out var schema))
            {
                return Task.FromResult<JsonElement?>(schema);
            }

            return _base.ResolveSchemaByReferenceAsync(schemaReference, cancellationToken);
        }

        public async Task<SchemaRegistry> BuildSchemaRegistryAsync(
            CancellationToken cancellationToken = default)
        {
            await _base.EnsureSchemasLoadedAsync(cancellationToken).ConfigureAwait(false);

            var requestSchemasById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var schema in _requestSchemas.Values.Distinct())
            {
                if (TryGetSchemaPayloadId(schema, out var schemaId)
                    && Uri.TryCreate(schemaId, UriKind.Absolute, out _))
                {
                    requestSchemasById[schemaId] = schema;
                }
            }

            return await BuildRegistryFromSchemasAsync(
                _base.schemaEntitiesById!,
                requestSchemasById,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
