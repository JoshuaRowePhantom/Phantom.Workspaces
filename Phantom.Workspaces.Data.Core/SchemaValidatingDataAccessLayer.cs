using System.Text.Json;
using Json.Schema;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs schema validation on data being updated on an underlying IDataAccessLayer.
/// </summary>
/// <remarks>
/// This data access layer expects UpdateRequests to have had merge processing already performed.
/// </remarks>
public sealed class SchemaValidatingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
{
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

        foreach (var change in request.Changes)
        {
            var validationErrors = await this.ValidateChangeAsync(change, requestSchemasByName, cancellationToken);
            if (validationErrors.Count == 0)
            {
                continue;
            }

            var entityId = change.EntityId ?? this.GetEntityId(change.Data);
            validationResults.Add(
                new EntityUpdateResult(
                    UpdateState.Failed,
                    entityId ?? default,
                    entityId ?? default,
                    null,
                    ConcurrencyMatchState.NotMatched,
                    null,
                    validationErrors));
        }

        if (validationResults.Count > 0)
        {
            return new UpdateResult(validationResults);
        }

        return await this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyCollection<UpdateError>> ValidateChangeAsync(
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

        var schemaReference = this.GetSchemaReference(data);
        if (schemaReference is null)
        {
            return Array.Empty<UpdateError>();
        }

        var schemaResult = await this.ResolveSchemaAsync(schemaReference, requestSchemasByName, cancellationToken);
        if (schemaResult is null)
        {
            return new[]
            {
                new UpdateError("Schema reference could not be resolved.", change.EntityId),
            };
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(this.GetSchemaText(schemaResult.Value));
        }
        catch (Exception exception)
        {
            return new[]
            {
                new UpdateError($"Schema is invalid: {exception.Message}", change.EntityId),
            };
        }

        var evaluation = schema.Evaluate(data);
        if (evaluation.IsValid)
        {
            return Array.Empty<UpdateError>();
        }

        return new[]
        {
            new UpdateError($"Entity does not conform to schema '{schemaReference}'.", change.EntityId),
        };
    }

    private string GetSchemaText(
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

    private string? GetSchemaReference(
        JsonElement entityObject)
    {
        if (!entityObject.TryGetProperty("$schema", out var schemaElement)
            || schemaElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return schemaElement.GetString();
    }

    private async Task<JsonElement?> ResolveSchemaAsync(
        string schemaReference,
        IReadOnlyDictionary<string, JsonElement> requestSchemasByName,
        CancellationToken cancellationToken)
    {
        if (requestSchemasByName.TryGetValue(schemaReference, out var requestSchema))
        {
            return requestSchema;
        }

        var getResult = await this.UnderlyingDataAccessLayer.GetAsync(
            new GetRequest(
                null,
                new[] { new EntityName(schemaReference) },
                null,
                new Timestamp?[] { null }),
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

        return null;
    }

    private IReadOnlyDictionary<string, JsonElement> GetSchemasFromRequest(
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

    private bool IsSchemaEntity(
        JsonElement entityObject)
    {
        return entityObject.TryGetProperty("$id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String;
    }

    private IReadOnlyCollection<string> GetEntityNames(
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
            }
        }

        return names;
    }

    private bool IsEntityMetadataProperty(
        string propertyName)
    {
        return propertyName.Equals("$id", StringComparison.Ordinal)
            || propertyName.Equals("entity-id", StringComparison.Ordinal)
            || propertyName.Equals("entity-types", StringComparison.Ordinal)
            || propertyName.Equals("names", StringComparison.Ordinal);
    }

    private EntityId? GetEntityId(
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
}
