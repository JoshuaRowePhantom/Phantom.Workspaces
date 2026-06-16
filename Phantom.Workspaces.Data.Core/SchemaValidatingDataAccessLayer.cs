using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Data.Serialization;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs schema validation on data being updated on an underlying IDataAccessLayer.
/// </summary>
/// <remarks>
/// This data access layer expects UpdateRequests to have had merge processing already performed.
/// Schemas are loaded fresh for each update from the request payload and the underlying IDataAccessLayer.
/// This class does not cache schema content between update calls.
/// </remarks>
public class SchemaValidatingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
{
    private const string JsonSchemasNamePrefix = "json-schemas";
    private static readonly string[] EntitySchemaNameComponents = { JsonSchemasNamePrefix, "https://schemas.workspaces.phantom.to/workspaces/data/core/entity.json" };
    private static readonly string EntitySchemaName = JsonSerializer.Serialize(EntitySchemaNameComponents);
    private const string JsonSchemaType = "json-schema";
    private const string Draft202012MetaSchema = "https://json-schema.org/draft/2020-12/schema";
    private const string EntityTypeSchemaName = "[\"entity-types\",\"entity\"]";
    private const string CustomEntityTypeKeyword = "x-entity-types";

    public SchemaValidatingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
        : base(underlyingDataAccessLayer)
    {
    }

    public override async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationResults = new List<EntityUpdateResult>();
        var schemaAccessor = this.CreateSchemaAccessor(request);
        var schemaRegistry = await this.BuildSchemaRegistryAsync(schemaAccessor, cancellationToken).ConfigureAwait(false);

        foreach (var change in request.Changes)
        {
            var validationErrors = await this.ValidateChangeAsync(change, schemaAccessor, schemaRegistry, cancellationToken).ConfigureAwait(false);
            if (validationErrors.Count == 0)
            {
                continue;
            }

            var entityId = change.EntityId ?? this.GetEntityId(change.Data);
            validationResults.Add(
                new EntityUpdateResult
                {
                    UpdateState = UpdateState.Failed,
                    RequestedEntityId = entityId ?? default,
                    ResultingEntityId = entityId ?? default,
                    ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                    Errors = validationErrors,
                });
        }

        if (validationResults.Count > 0)
        {
            return new UpdateResult
            {
                EntityResults = validationResults,
            };
        }

        return await this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task<IReadOnlyCollection<UpdateError>> ValidateChangeAsync(
        EntityChange change,
        ISchemaAccessor schemaAccessor,
        SchemaRegistry schemaRegistry,
        CancellationToken cancellationToken)
    {
        if (change.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<UpdateError>();
        }

        var applicableSchemas = await this.ResolveApplicableSchemasAsync(data, schemaAccessor, cancellationToken).ConfigureAwait(false);
        if (applicableSchemas.Count == 0)
        {
            return Array.Empty<UpdateError>();
        }

        var errors = new List<UpdateError>();
        var resolvedSchemas = new List<ApplicableSchema>();
        foreach (var applicableSchema in applicableSchemas)
        {
            if (applicableSchema.SchemaEntity is null)
            {
                errors.Add(
                    new UpdateError
                    {
                        Message = $"Schema reference '{applicableSchema.SchemaReference}' could not be resolved.",
                        RelatedEntityId = change.EntityId,
                    });
                continue;
            }

            resolvedSchemas.Add(
                applicableSchema with
                {
                    SchemaEntity = applicableSchema.SchemaEntity.Value,
                });
        }

        if (errors.Count > 0 || resolvedSchemas.Count == 0)
        {
            return errors;
        }

        var shouldCloseUnevaluatedProperties = resolvedSchemas.Any(
            schema => !this.IsBaseEntitySchema(schema));

        JsonSchema composedSchema;
        try
        {
            composedSchema = JsonSchema.FromText(
                this.BuildComposedSchemaText(resolvedSchemas, shouldCloseUnevaluatedProperties),
                new BuildOptions
                {
                    SchemaRegistry = schemaRegistry,
                });
        }
        catch (Exception exception)
        {
            errors.Add(
                new UpdateError
                {
                    Message = $"Composed schema is invalid: {exception.Message}",
                    RelatedEntityId = change.EntityId,
                });
            return errors;
        }

        EvaluationResults evaluation;
        evaluation = composedSchema.Evaluate(
            data,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                PreserveDroppedAnnotations = true,
            });
        if (!evaluation.IsValid)
        {
            evaluation.ToList();
            var detailedErrors = this.GetDetailedValidationErrors(evaluation)
                .Take(10)
                .ToArray();
            var detailsSuffix = detailedErrors.Length == 0
                ? $" Details: {evaluation}"
                : $" Details: {string.Join(" | ", detailedErrors)}";
            errors.Add(
                new UpdateError
                {
                    Message = $"Entity does not conform to schema composition for references [{string.Join(", ", applicableSchemas.Select(static schema => schema.SchemaReference))}].{detailsSuffix}",
                    RelatedEntityId = change.EntityId,
                });
        }

        return errors;
    }

    protected virtual ISchemaAccessor CreateSchemaAccessor(
        UpdateRequest request)
    {
        return new SchemaAccessor(this.UnderlyingDataAccessLayer, request);
    }

    protected virtual Task<SchemaRegistry> BuildSchemaRegistryAsync(
        ISchemaAccessor schemaAccessor,
        CancellationToken cancellationToken)
    {
        return schemaAccessor.BuildSchemaRegistryAsync(cancellationToken);
    }

    private void TryAddSchemaEntityById(
        IDictionary<string, JsonElement> schemaEntitiesById,
        JsonElement schemaEntity)
    {
        if (!this.TryGetSchemaPayloadId(schemaEntity, out var id))
        {
            return;
        }

        if (!Uri.TryCreate(id, UriKind.Absolute, out _))
        {
            return;
        }

        schemaEntitiesById[id] = schemaEntity;
    }

    private IReadOnlyCollection<string> GetDetailedValidationErrors(
        EvaluationResults evaluation)
    {
        var messages = new List<string>();
        this.CollectEvaluationErrors(evaluation, messages);
        return messages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void CollectEvaluationErrors(
        EvaluationResults evaluation,
        ICollection<string> messages)
    {
        var nodeHasError = false;
        if (evaluation.Errors is { Count: > 0 })
        {
            var location = evaluation.InstanceLocation.ToString();
            foreach (var error in evaluation.Errors)
            {
                var keyword = string.IsNullOrWhiteSpace(error.Key) ? "schema" : error.Key;
                var pathPrefix = string.IsNullOrWhiteSpace(location) || location == "#"
                    ? string.Empty
                    : $" at '{location}'";
                messages.Add($"{keyword}{pathPrefix}: {error.Value}");
            }
            nodeHasError = true;
        }

        if (!nodeHasError && !evaluation.IsValid)
        {
            var instanceLocation = evaluation.InstanceLocation.ToString();
            var schemaLocation = evaluation.SchemaLocation?.ToString() ?? "<unknown-schema-location>";
            var instanceText = string.IsNullOrWhiteSpace(instanceLocation) || instanceLocation == "#"
                ? "$"
                : instanceLocation;
            messages.Add($"validation failed at instance '{instanceText}' against '{schemaLocation}'");
        }

        if (evaluation.Details is not { Count: > 0 })
        {
            return;
        }

        foreach (var detail in evaluation.Details.Where(static detail => !detail.IsValid))
        {
            this.CollectEvaluationErrors(detail, messages);
        }
    }

    protected async Task<IReadOnlyCollection<ApplicableSchema>> ResolveApplicableSchemasAsync(
        JsonElement entityObject,
        ISchemaAccessor schemaAccessor,
        CancellationToken cancellationToken)
    {
        var schemaReferences = this.GetSchemaReferencesForEntity(entityObject)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (schemaReferences.Length == 0)
        {
            return Array.Empty<ApplicableSchema>();
        }

        var applicableSchemas = new List<ApplicableSchema>(schemaReferences.Length);
        foreach (var schemaReference in schemaReferences)
        {
            var schemaEntity = await schemaAccessor.ResolveSchemaByReferenceAsync(schemaReference, cancellationToken).ConfigureAwait(false);
            applicableSchemas.Add(
                new ApplicableSchema
                {
                    SchemaReference = schemaReference,
                    SchemaEntity = schemaEntity,
                });
        }

        return applicableSchemas;
    }

    protected Task<IReadOnlyCollection<ApplicableSchema>> ResolveApplicableSchemasAsync(
        JsonElement entityObject,
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
        CancellationToken cancellationToken)
    {
        return this.ResolveApplicableSchemasAsync(
            entityObject,
            new SchemaAccessor(this.UnderlyingDataAccessLayer, requestSchemasByName),
            cancellationToken);
    }

    protected IReadOnlyCollection<string> GetSchemaReferencesForEntity(
        JsonElement entityObject)
    {
        var references = new List<string>
        {
            EntitySchemaName,
        };

        var explicitSchemaReference = this.GetSchemaReference(entityObject);
        if (!string.IsNullOrWhiteSpace(explicitSchemaReference)
            && !string.Equals(explicitSchemaReference, Draft202012MetaSchema, StringComparison.Ordinal))
        {
            references.Add(explicitSchemaReference);
        }

        foreach (var entityTypeName in this.GetExplicitEntityTypeNames(entityObject))
        {
            references.Add(JsonSerializer.Serialize(new[] { "entity-types", entityTypeName }));

            if (string.Equals(entityTypeName, "entity-type", StringComparison.Ordinal))
            {
                references.Add(JsonSerializer.Serialize(new[] { "entity-types", JsonSchemaType }));
            }
        }

        return references;
    }

    protected string GetSchemaText(
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
                this.WriteElementWithoutCustomKeywords(schemaPayload, payloadWriter);
                payloadWriter.Flush();
                return System.Text.Encoding.UTF8.GetString(payloadStream.ToArray());
            }
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        foreach (var property in schemaEntity.EnumerateObject())
        {
            if (this.IsEntityMetadataProperty(property.Name))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            this.WriteElementWithoutCustomKeywords(property.Value, writer);
        }

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    protected void WriteElementWithoutCustomKeywords(
        JsonElement element,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, CustomEntityTypeKeyword, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.Equals(property.Name, "x-default-mime-type", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    this.WriteElementWithoutCustomKeywords(property.Value, writer);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    this.WriteElementWithoutCustomKeywords(item, writer);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    protected string? GetSchemaReference(
        JsonElement entityObject)
    {
        if (!entityObject.TryGetProperty("$schema", out var schemaElement)
            || schemaElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return schemaElement.GetString();
    }

    protected async Task<JsonElement?> ResolveSchemaAsync(
        string schemaReference,
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
        CancellationToken cancellationToken)
    {
        return await this.ResolveSchemaAsync(
            schemaReference,
            requestSchemasByName,
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
    }

    protected async Task<JsonElement?> ResolveSchemaAsync(
        string schemaReference,
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
        ISet<string> visitedSchemaReferences,
        CancellationToken cancellationToken)
    {
        var schemaNames = this.GetSchemaEntityNames(schemaReference);
        if (!schemaNames.Any(visitedSchemaReferences.Add))
        {
            return null;
        }

        foreach (var schemaName in schemaNames)
        {
            if (requestSchemasByName.TryGetValue(schemaName, out var requestSchema))
            {
                return requestSchema;
            }
        }

        foreach (var schemaName in schemaNames)
        {
            if (!this.TryParseEntityName(schemaName, out var parsedSchemaName))
            {
                continue;
            }

            var getResult = await this.UnderlyingDataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityName = parsedSchemaName,
                        },
                    ],
                    Timestamps = new Timestamp?[] { null },
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var batch in getResult.Batches)
            {
                foreach (var entity in batch.Entities)
                {
                    if (entity.Data is { } data && data.ValueKind == JsonValueKind.Object)
                    {
                        return data;
                    }
                }
            }
        }

#pragma warning disable CS0618
        var exportResult = await this.UnderlyingDataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
        foreach (var entity in exportResult.ChangeBatches.SelectMany(static batch => batch.Entities))
        {
            if (entity.Data is not { ValueKind: JsonValueKind.Object } data)
            {
                continue;
            }

            if (this.GetEntityNames(data).Intersect(schemaNames, StringComparer.Ordinal).Any())
            {
                return data;
            }
        }

        return null;
    }

    protected IReadOnlyDictionary<string, JsonElement> GetSchemasFromRequest(
        UpdateRequest request)
    {
        var schemasByName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var change in request.Changes)
        {
            if (change.Data is not { } data || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!this.IsSchemaEntity(data))
            {
                continue;
            }

            foreach (var name in this.GetEntityNames(data))
            {
                schemasByName[name] = data;
            }
        }

        return schemasByName;
    }

    protected bool IsSchemaEntity(
        JsonElement entityObject)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityObject);
        return schemaEntityDocument is not null
            && schemaEntityDocument.IsSchemaEntity();
    }

    protected IReadOnlyCollection<string> GetEntityNames(
        JsonElement entityObject)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityObject);
        return schemaEntityDocument?.GetCanonicalNames() ?? Array.Empty<string>();
    }

    protected HashSet<string> GetExplicitEntityTypeNames(
        JsonElement entityData)
    {
        var schemaEntityDocument = SchemaEntityDocument.Deserialize(entityData);
        return schemaEntityDocument?.GetExplicitEntityTypeNames()
               ?? new HashSet<string>(StringComparer.Ordinal);
    }

    protected HashSet<string> GetEntityTypeNames(
        JsonElement entityData)
    {
        return this.GetExplicitEntityTypeNames(entityData);
    }

    protected bool TryGetCanonicalNameFromArray(
        JsonElement value,
        out string canonicalName)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            canonicalName = string.Empty;
            return false;
        }

        var components = new List<string>();
        foreach (var component in value.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(component.GetString()))
            {
                canonicalName = string.Empty;
                return false;
            }

            components.Add(component.GetString()!);
        }

        if (components.Count == 0)
        {
            canonicalName = string.Empty;
            return false;
        }

        canonicalName = JsonSerializer.Serialize(components);
        return true;
    }

    private bool TryParseEntityName(
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

    protected bool IsEntityMetadataProperty(
        string propertyName)
    {
        return propertyName.Equals("$id", StringComparison.Ordinal)
            || propertyName.Equals("entity-id", StringComparison.Ordinal)
            || propertyName.Equals("entity-types", StringComparison.Ordinal)
            || propertyName.Equals("names", StringComparison.Ordinal)
            || propertyName.Equals("unevaluatedProperties", StringComparison.Ordinal);
    }

    protected EntityId? GetEntityId(
        JsonElement? data)
    {
        if (data is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!value.TryGetProperty("entity-id", out var entityIdElement)
            || entityIdElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entityIdElement.GetString(), out var entityGuid))
        {
            return null;
        }

        return new EntityId(entityGuid);
    }

    protected sealed record ApplicableSchema
    {
        public required string SchemaReference { get; init; }

        public JsonElement? SchemaEntity { get; init; }
    }

    private bool IsEntityTypeSchemaReference(
        string schemaReference)
    {
        if (string.IsNullOrWhiteSpace(schemaReference)
            || !schemaReference.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaReference);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 2)
            {
                return false;
            }

            var first = document.RootElement[0];
            var second = document.RootElement[1];
            return first.ValueKind == JsonValueKind.String
                && second.ValueKind == JsonValueKind.String
                && string.Equals(first.GetString(), "entity-types", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(second.GetString());
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetSchemaPayloadId(
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

    private bool IsBaseEntitySchema(
        ApplicableSchema schema)
    {
        return string.Equals(schema.SchemaReference, EntitySchemaName, StringComparison.Ordinal);
    }

    private IReadOnlyCollection<string> GetSchemaEntityNames(
        string schemaReference)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(schemaReference))
        {
            if (schemaReference.StartsWith("[", StringComparison.Ordinal))
            {
                names.Add(schemaReference);
            }
            else
            {
                names.Add(JsonSerializer.Serialize(new[] { JsonSchemasNamePrefix, schemaReference }));
            }

            if (!names.Contains(schemaReference, StringComparer.Ordinal))
            {
                names.Add(schemaReference);
            }
        }

        return names;
    }

    private string BuildComposedSchemaText(
        IReadOnlyCollection<ApplicableSchema> schemas,
        bool closeUnevaluatedProperties)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WritePropertyName("allOf");
        writer.WriteStartArray();
        foreach (var schema in schemas)
        {
            if (schema.SchemaEntity is { } schemaEntity
                && schemaEntity.TryGetProperty("schema", out _))
            {
                if (this.TryGetSchemaPayloadId(schemaEntity, out var payloadId)
                    && Uri.TryCreate(payloadId, UriKind.Absolute, out _))
                {
                    writer.WriteStartObject();
                    writer.WriteString("$ref", payloadId);
                    writer.WriteEndObject();
                }
                else
                {
                    using var schemaDocument = JsonDocument.Parse(this.GetSchemaText(schemaEntity));
                    schemaDocument.RootElement.WriteTo(writer);
                }
            }
            else if (schema.SchemaEntity is { } schemaEntityWithId
                && schemaEntityWithId.TryGetProperty("$id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                writer.WriteStartObject();
                writer.WriteString("$ref", idElement.GetString());
                writer.WriteEndObject();
            }
            else if (schema.SchemaEntity is { } inlineSchemaEntity)
            {
                using var schemaDocument = JsonDocument.Parse(this.GetSchemaText(inlineSchemaEntity));
                schemaDocument.RootElement.WriteTo(writer);
            }
        }

        writer.WriteEndArray();
        if (closeUnevaluatedProperties)
        {
            writer.WriteBoolean("unevaluatedProperties", false);
        }
        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
