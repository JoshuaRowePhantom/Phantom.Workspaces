using System.Text.Json;

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

        foreach (var change in request.Changes)
        {
            var validationErrors = await this.ValidateChangeAsync(change, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        if (change.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<UpdateError>();
        }

        var schemaReference = this.GetSchemaReference(data);
        if (schemaReference is null)
        {
            return Array.Empty<UpdateError>();
        }

        var schemaResult = await this.ResolveSchemaAsync(schemaReference, cancellationToken);
        if (schemaResult is null)
        {
            return new[]
            {
                new UpdateError("Schema reference could not be resolved.", change.EntityId),
            };
        }

        return Array.Empty<UpdateError>();
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
        CancellationToken cancellationToken)
    {
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
