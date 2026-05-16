using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Performs schema validation on data being updated on an underlying IDataAccessLayer.
/// </summary>
/// <remarks>
/// This data access layer expects UpdateRequests to have had merge processing already performed.
/// </remarks>
public sealed class SchemaValidatingDataAccessLayer : IDataAccessLayer
{
    private readonly IDataAccessLayer underlyingDataAccessLayer;
    private readonly BuiltinSchemaResolver builtinSchemaResolver;

    public SchemaValidatingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
        : this(
            underlyingDataAccessLayer,
            new BuiltinSchemaResolver())
    {
    }

    public SchemaValidatingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer,
        BuiltinSchemaResolver builtinSchemaResolver)
    {
        this.underlyingDataAccessLayer = underlyingDataAccessLayer;
        this.builtinSchemaResolver = builtinSchemaResolver;
    }

    public Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.ExportAsync(request, cancellationToken);
    }

    public Task<GetResult> GetAsync(
        GetRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.GetAsync(request, cancellationToken);
    }

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
        GetChangedEntitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken);
    }

    public Task<GetHistoryResult> GetHistoryAsync(
        GetHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.GetHistoryAsync(request, cancellationToken);
    }

    public Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return this.underlyingDataAccessLayer.QueryAsync(request, cancellationToken);
    }

    public async Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var availableSchemas = await this.LoadAvailableSchemasAsync(cancellationToken);
        foreach (var change in request.Changes)
        {
            this.RegisterSchemaFromEntity(change.Data, availableSchemas);
        }

        var validationResults = new List<EntityUpdateResult>();

        foreach (var change in request.Changes)
        {
            var validationErrors = this.ValidateChange(change, availableSchemas);
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
                    validationErrors));
        }

        if (validationResults.Count > 0)
        {
            return new UpdateResult(validationResults);
        }

        return await this.underlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
    }

    private async Task<Dictionary<string, JsonObject>> LoadAvailableSchemasAsync(
        CancellationToken cancellationToken)
    {
        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var schemaId in this.builtinSchemaResolver.SchemaIds)
        {
            schemas[schemaId] = this.builtinSchemaResolver.GetSchema(schemaId);
        }

        var exportResult = await this.underlyingDataAccessLayer.ExportAsync(
            new ExportRequest(null),
            cancellationToken);

        foreach (var batch in exportResult.ChangeBatches)
        {
            foreach (var entity in batch.Entities)
            {
                this.RegisterSchemaFromEntity(entity.Data, schemas);
            }
        }

        return schemas;
    }

    private IReadOnlyCollection<UpdateError> ValidateChange(
        EntityChange change,
        Dictionary<string, JsonObject> availableSchemas)
    {
        var errors = new List<UpdateError>();
        if (change.Data is not JsonObject entityObject)
        {
            return errors;
        }

        this.RegisterSchemaFromEntity(entityObject, availableSchemas);

        if (!entityObject.TryGetPropertyValue("schema", out var schemaNode) || schemaNode is null)
        {
            return errors;
        }

        if (!this.TryResolveSchema(schemaNode, availableSchemas, out var schemaObject))
        {
            errors.Add(new UpdateError("Schema reference could not be resolved.", change.EntityId));
            return errors;
        }

        var schemaText = schemaObject.ToJsonString();
        var instanceText = entityObject.ToJsonString();
        var schema = JsonSchema.FromText(schemaText);
        var instance = JsonDocument.Parse(instanceText).RootElement;
        var evaluation = schema.Evaluate(instance);

        if (!evaluation.IsValid)
        {
            errors.Add(
                new UpdateError(
                    "Schema validation failed.",
                    change.EntityId));
        }

        return errors;
    }

    private void RegisterSchemaFromEntity(
        JsonNode? entityData,
        Dictionary<string, JsonObject> availableSchemas)
    {
        if (entityData is not JsonObject entityObject)
        {
            return;
        }

        if (!entityObject.TryGetPropertyValue("schema", out var schemaNode)
            || schemaNode is not JsonObject schemaObject)
        {
            return;
        }

        if (schemaObject.TryGetPropertyValue("$id", out var schemaIdNode)
            && schemaIdNode is JsonValue schemaIdValue
            && schemaIdValue.TryGetValue<string>(out var schemaId)
            && !string.IsNullOrWhiteSpace(schemaId))
        {
            availableSchemas[schemaId] = (JsonObject)schemaObject.DeepClone();
        }
    }

    private bool TryResolveSchema(
        JsonNode schemaNode,
        Dictionary<string, JsonObject> availableSchemas,
        out JsonObject schemaObject)
    {
        if (schemaNode is JsonObject directSchemaObject)
        {
            schemaObject = (JsonObject)directSchemaObject.DeepClone();
            return true;
        }

        if (schemaNode is JsonValue schemaValue
            && schemaValue.TryGetValue<string>(out var schemaId)
            && availableSchemas.TryGetValue(schemaId, out schemaObject!))
        {
            return true;
        }

        schemaObject = null!;
        return false;
    }

    private EntityId? GetEntityId(
        JsonNode? data)
    {
        if (data is not JsonObject entityObject)
        {
            return null;
        }

        if (!entityObject.TryGetPropertyValue("entity-id", out var entityIdNode)
            || entityIdNode is not JsonValue entityIdValue
            || !entityIdValue.TryGetValue<string>(out var entityIdText)
            || !Guid.TryParse(entityIdText, out var entityGuid))
        {
            return null;
        }

        return new EntityId(entityGuid);
    }
}
