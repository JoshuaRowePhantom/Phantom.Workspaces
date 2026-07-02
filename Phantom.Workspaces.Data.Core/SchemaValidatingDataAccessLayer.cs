using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Data.Serialization;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs schema validation on data being updated on an underlying IDataAccessLayer.
/// </summary>
/// <remarks>
/// This data access layer expects UpdateRequests to have had merge processing already performed.
/// Schemas are loaded from the request payload and the underlying IDataAccessLayer, then cached
/// across calls. The cache is invalidated whenever a json-schema entity is successfully written.
/// </remarks>
public class SchemaValidatingDataAccessLayer : BaseUpdateProcessingDataAccessLayer, IEntitySchemaComposer
{
    private const string EntitySchemaUri = "https://schemas.workspaces.phantom.to/workspaces/data/core/entity.json";
    private const string Draft202012MetaSchema = "https://json-schema.org/draft/2020-12/schema";
    private const string EntityTypeType = "entity-type";
    private static readonly IReadOnlySet<string> EmptyEntityTypeNames = new HashSet<string>(StringComparer.Ordinal);

    private readonly SchemaAccessor _schemaAccessor;
    private IReadOnlySet<string>? _cachedRegisteredEntityTypeNames;

    public SchemaValidatingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer,
        SchemaAccessor schemaAccessor)
        : base(underlyingDataAccessLayer)
    {
        _schemaAccessor = schemaAccessor;
    }

    public SchemaValidatingDataAccessLayer(IDataAccessLayer underlyingDataAccessLayer)
        : this(underlyingDataAccessLayer, new SchemaAccessor(underlyingDataAccessLayer))
    {
    }

    public override async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationResults = new List<EntityUpdateResult>();
        var requestHasSchemas = request.Changes.Any(change => change.Data is { ValueKind: JsonValueKind.Object } data && this.IsSchemaEntity(data));

        var schemaAccessor = _schemaAccessor.CreateOverlayForRequest(request);
        var schemaRegistry = await schemaAccessor.BuildSchemaRegistryAsync(cancellationToken).ConfigureAwait(false);

        await this.EnsureEntityTypeNamesCachedAsync(cancellationToken).ConfigureAwait(false);
        var requestEntityTypeNames = GetEntityTypeNamesFromRequest(request);

        foreach (var change in request.Changes)
        {
            var entityId = change.EntityId ?? this.GetEntityId(change.Data);

            var discriminatorErrors = this.ValidateEntityTypeDiscriminators(
                change, _cachedRegisteredEntityTypeNames, requestEntityTypeNames);
            if (discriminatorErrors.Count > 0)
            {
                validationResults.Add(
                    new EntityUpdateResult
                    {
                        UpdateState = UpdateState.Failed,
                        RequestedEntityId = entityId ?? default,
                        ResultingEntityId = entityId ?? default,
                        ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                        Errors = discriminatorErrors,
                    });
                continue;
            }

            var validationErrors = await this.ValidateChangeAsync(change, schemaAccessor, schemaRegistry, cancellationToken).ConfigureAwait(false);
            if (validationErrors.Count == 0)
            {
                continue;
            }

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

        var result = await this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
        if (requestHasSchemas)
        {
            _schemaAccessor.InvalidateOnSchemaUpdate();
            _cachedRegisteredEntityTypeNames = null;
        }

        return result;
    }

    /// <summary>
    /// Validates the supplied entity data against the composed schema for its entity types without
    /// persisting anything, returning human-readable validation error messages. Shares the exact
    /// composition and evaluation logic used during updates.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> GetValidationErrorsAsync(
        JsonElement entityData,
        CancellationToken cancellationToken = default)
    {
        var schemaAccessor = _schemaAccessor;
        var schemaRegistry = await this.BuildSchemaRegistryAsync(schemaAccessor, cancellationToken).ConfigureAwait(false);
        await this.EnsureEntityTypeNamesCachedAsync(cancellationToken).ConfigureAwait(false);
        var change = new EntityChange
        {
            Data = entityData,
            EntityChangeMode = EntityChangeMode.Replace,
        };

        var discriminatorErrors = this.ValidateEntityTypeDiscriminators(
            change, _cachedRegisteredEntityTypeNames, EmptyEntityTypeNames);
        if (discriminatorErrors.Count > 0)
        {
            return discriminatorErrors.Select(static error => error.Message).ToArray();
        }

        var errors = await this.ValidateChangeAsync(change, schemaAccessor, schemaRegistry, cancellationToken).ConfigureAwait(false);
        return errors.Select(static error => error.Message).ToArray();
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

        var onlyAbstractTypesError = this.GetOnlyAbstractEntityTypesError(resolvedSchemas, change.EntityId);
        if (onlyAbstractTypesError is not null)
        {
            errors.Add(onlyAbstractTypesError);
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
                    Dialect = WorkspacesSchemaDialect.AllowingUnknownKeywords,
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
        return _schemaAccessor.CreateOverlayForRequest(request);
    }

    protected virtual Task<SchemaRegistry> BuildSchemaRegistryAsync(
        ISchemaAccessor schemaAccessor,
        CancellationToken cancellationToken)
    {
        return schemaAccessor.BuildSchemaRegistryAsync(cancellationToken);
    }

    private async Task EnsureEntityTypeNamesCachedAsync(CancellationToken cancellationToken)
    {
        if (_cachedRegisteredEntityTypeNames is null)
        {
            await _schemaAccessor.EnsureSchemasLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_schemaAccessor.SchemaEntitiesById is { } schemaEntities
                && _cachedRegisteredEntityTypeNames is null)
            {
                _cachedRegisteredEntityTypeNames = BuildRegisteredEntityTypeNames(schemaEntities);
            }
        }
    }

    private static IReadOnlySet<string> BuildRegisteredEntityTypeNames(
        IReadOnlyDictionary<string, JsonElement> schemaEntitiesById)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schemaEntity in schemaEntitiesById.Values)
        {
            TryCollectEntityTypeDiscriminatorName(schemaEntity, names);
        }

        return names;
    }

    private static void TryCollectEntityTypeDiscriminatorName(
        JsonElement entityData,
        HashSet<string> names)
    {
        var doc = SchemaEntityDocument.Deserialize(entityData);
        if (doc is null || doc.Names is null)
        {
            return;
        }

        if (!doc.GetExplicitEntityTypeNames().Contains(EntityTypeType))
        {
            return;
        }

        foreach (var nameComponents in doc.Names)
        {
            if (nameComponents is { Length: 2 }
                && string.Equals(nameComponents[0], "entity-types", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(nameComponents[1]))
            {
                names.Add(nameComponents[1]);
                break;
            }
        }
    }

    private static IReadOnlySet<string> GetEntityTypeNamesFromRequest(UpdateRequest request)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in request.Changes)
        {
            if (change.Data is { ValueKind: JsonValueKind.Object } data)
            {
                TryCollectEntityTypeDiscriminatorName(data, names);
            }
        }

        return names;
    }

    private IReadOnlyCollection<UpdateError> ValidateEntityTypeDiscriminators(
        EntityChange change,
        IReadOnlySet<string>? registeredEntityTypeNames,
        IReadOnlySet<string> requestEntityTypeNames)
    {
        if (change.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return Array.Empty<UpdateError>();
        }

        var discriminators = this.GetExplicitEntityTypeNames(data);
        if (discriminators.Count == 0)
        {
            return Array.Empty<UpdateError>();
        }

        List<UpdateError>? errors = null;
        foreach (var discriminator in discriminators)
        {
            if (registeredEntityTypeNames?.Contains(discriminator) == true
                || requestEntityTypeNames.Contains(discriminator))
            {
                continue;
            }

            errors ??= new List<UpdateError>();
            errors.Add(new UpdateError
            {
                Message = $"Entity-type discriminator '{discriminator}' is not a registered entity type.",
                RelatedEntityId = change.EntityId,
            });
        }

        return (IReadOnlyCollection<UpdateError>?)errors ?? Array.Empty<UpdateError>();
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
            _schemaAccessor.CreateOverlayForRequest(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = string.Empty } },
                    Changes = requestSchemasByName.Values
                        .Distinct()
                        .Select(static schema => new EntityChange
                        {
                            Data = schema,
                            EntityChangeMode = EntityChangeMode.Replace,
                        })
                        .ToArray(),
                }),
            cancellationToken);
    }

    protected IReadOnlyCollection<string> GetSchemaReferencesForEntity(
        JsonElement entityObject)
    {
        var references = new List<string>
        {
            EntitySchemaUri,
        };

        var explicitSchemaReference = this.GetSchemaReference(entityObject);
        if (!string.IsNullOrWhiteSpace(explicitSchemaReference)
            && !string.Equals(explicitSchemaReference, Draft202012MetaSchema, StringComparison.Ordinal))
        {
            references.Add(explicitSchemaReference);
        }

        foreach (var entityTypeName in this.GetExplicitEntityTypeNames(entityObject))
        {
            references.Add(entityTypeName);
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
            if (this.IsEntityMetadataProperty(property.Name))
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

    private static bool IsEntityTypeSchemaReference(string schemaReference)
    {
        return !string.IsNullOrWhiteSpace(schemaReference)
            && !Uri.TryCreate(schemaReference, UriKind.Absolute, out _);
    }

    private UpdateError? GetOnlyAbstractEntityTypesError(
        IReadOnlyList<ApplicableSchema> resolvedSchemas,
        EntityId? entityId)
    {
        var entityTypeSchemas = resolvedSchemas
            .Where(static schema => IsEntityTypeSchemaReference(schema.SchemaReference))
            .ToArray();
        if (entityTypeSchemas.Length == 0)
        {
            return null;
        }

        if (entityTypeSchemas.Any(static schema => !IsAbstractEntityType(schema.SchemaEntity)))
        {
            return null;
        }

        var typeNames = entityTypeSchemas
            .Select(static schema => GetEntityTypeNameFromSchemaReference(schema.SchemaReference));
        return new UpdateError
        {
            Message = $"Entity declares only abstract entity types [{string.Join(", ", typeNames)}]. At least one concrete (non-abstract) entity type is required.",
            RelatedEntityId = entityId,
        };
    }

    private static bool IsAbstractEntityType(
        JsonElement? schemaEntity)
    {
        return schemaEntity is { ValueKind: JsonValueKind.Object } entity
            && entity.TryGetProperty("abstract", out var abstractValue)
            && abstractValue.ValueKind == JsonValueKind.True;
    }

    private static string GetEntityTypeNameFromSchemaReference(string schemaReference)
        => schemaReference;

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

    private bool IsBaseEntitySchema(ApplicableSchema schema)
    {
        return string.Equals(schema.SchemaReference, EntitySchemaUri, StringComparison.Ordinal);
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
