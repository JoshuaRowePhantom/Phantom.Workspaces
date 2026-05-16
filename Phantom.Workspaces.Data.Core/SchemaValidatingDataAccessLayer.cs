using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs schema validation on data being updated on an underlying IDataAccessLayer.
/// </summary>
/// <remarks>
/// This data access layer expects UpdateRequests to have had merge processing already performed.
/// </remarks>
public class SchemaValidatingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
{
    private const string JsonSchemaType = "json-schema";

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
        var requestSchemasByName = this.GetSchemasFromRequest(request);
        await this.RegisterSchemasAsync(requestSchemasByName, cancellationToken);

        foreach (var change in request.Changes)
        {
            var validationErrors = await this.ValidateChangeAsync(change, requestSchemasByName, cancellationToken);
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

        return await this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
    }

    protected virtual async Task<IReadOnlyCollection<UpdateError>> ValidateChangeAsync(
        EntityChange change,
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
        CancellationToken cancellationToken)
    {
        if (change.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<UpdateError>();
        }

        if (this.IsSchemaEntity(data))
        {
            return Array.Empty<UpdateError>();
        }

        var applicableSchemas = await this.ResolveApplicableSchemasAsync(data, requestSchemasByName, cancellationToken);
        if (applicableSchemas.Count == 0)
        {
            return Array.Empty<UpdateError>();
        }

        var errors = new List<UpdateError>();
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

            JsonSchema schema;
            try
            {
                schema = JsonSchema.FromText(this.GetSchemaText(applicableSchema.SchemaEntity.Value));
            }
            catch (Exception exception)
            {
                errors.Add(
                    new UpdateError
                    {
                        Message = $"Schema '{applicableSchema.SchemaReference}' is invalid: {exception.Message}",
                        RelatedEntityId = change.EntityId,
                    });
                continue;
            }

            var evaluation = schema.Evaluate(
                data,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.Hierarchical,
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
                        Message = $"Entity does not conform to schema '{applicableSchema.SchemaReference}'.{detailsSuffix}",
                        RelatedEntityId = change.EntityId,
                    });
            }
        }

        return errors;
    }

    protected virtual async Task RegisterSchemasAsync(
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
        CancellationToken cancellationToken)
    {
        var schemaEntitiesById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var schemaEntity in requestSchemasByName.Values)
        {
            this.TryAddSchemaEntityById(schemaEntitiesById, schemaEntity);
        }

        var exportResult = await this.UnderlyingDataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken);
        foreach (var entityData in exportResult.ChangeBatches
                     .SelectMany(static batch => batch.Entities)
                     .Select(static entity => entity.Data)
                     .Where(static data => data is { ValueKind: JsonValueKind.Object })
                     .Select(static data => data!.Value))
        {
            if (!this.IsSchemaEntity(entityData))
            {
                continue;
            }

            this.TryAddSchemaEntityById(schemaEntitiesById, entityData);
        }

        foreach (var pair in schemaEntitiesById)
        {
            try
            {
                var schema = JsonSchema.FromText(this.GetSchemaText(pair.Value));
                SchemaRegistry.Global.Register(new Uri(pair.Key, UriKind.Absolute), schema);
            }
            catch
            {
                // Invalid schemas are reported during per-entity validation.
            }
        }
    }

    private void TryAddSchemaEntityById(
        IDictionary<string, JsonElement> schemaEntitiesById,
        JsonElement schemaEntity)
    {
        if (!schemaEntity.TryGetProperty("$id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            return;
        }

        var id = idElement.GetString()!;
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
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
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
            var schemaEntity = await this.ResolveSchemaAsync(schemaReference, requestSchemasByName, cancellationToken);
            applicableSchemas.Add(
                new ApplicableSchema
                {
                    SchemaReference = schemaReference,
                    SchemaEntity = schemaEntity,
                });
        }

        return applicableSchemas;
    }

    protected IReadOnlyCollection<string> GetSchemaReferencesForEntity(
        JsonElement entityObject)
    {
        var references = new List<string>();
        var explicitSchemaReference = this.GetSchemaReference(entityObject);
        if (!string.IsNullOrWhiteSpace(explicitSchemaReference))
        {
            references.Add(explicitSchemaReference);
        }

        foreach (var entityTypeName in this.GetEntityTypeNames(entityObject))
        {
            references.Add(JsonSerializer.Serialize(new[] { "entity-types", entityTypeName }));
        }

        return references;
    }

    protected string GetSchemaText(
        JsonElement schemaEntity)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        foreach (var property in schemaEntity.EnumerateObject())
        {
            if (this.IsEntityMetadataProperty(property.Name))
            {
                continue;
            }

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
        if (requestSchemasByName.TryGetValue(schemaReference, out var requestSchema))
        {
            return requestSchema;
        }

        var getResult = await this.UnderlyingDataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName(schemaReference),
                    },
                ],
                Timestamps = new Timestamp?[] { null },
            },
            cancellationToken);

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

        var exportResult = await this.UnderlyingDataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken);
        foreach (var entity in exportResult.ChangeBatches.SelectMany(static batch => batch.Entities))
        {
        if (entity.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            continue;
        }

        if (this.GetEntityNames(data).Contains(schemaReference, StringComparer.Ordinal))
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
        return this.GetEntityTypeNames(entityObject).Contains(JsonSchemaType)
            || entityObject.TryGetProperty("$id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String;
    }

    protected IReadOnlyCollection<string> GetEntityNames(
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
            if (nameElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                names.Add(nameElement.GetString()!);
                continue;
            }

            if (nameElement.ValueKind == JsonValueKind.Array
                && this.TryGetCanonicalNameFromArray(nameElement, out var canonicalName))
            {
                names.Add(canonicalName);
            }
        }

        return names;
    }

    protected HashSet<string> GetEntityTypeNames(
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

    protected bool IsEntityMetadataProperty(
        string propertyName)
    {
        return propertyName.Equals("$id", StringComparison.Ordinal)
            || propertyName.Equals("entity-id", StringComparison.Ordinal)
            || propertyName.Equals("entity-types", StringComparison.Ordinal)
            || propertyName.Equals("names", StringComparison.Ordinal);
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
}
