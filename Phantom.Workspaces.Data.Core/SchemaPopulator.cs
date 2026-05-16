using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Populates the initial built-in schemas and entities into a data access layer.
/// </summary>
public sealed class SchemaPopulator
{
    private readonly IDataAccessLayer dataAccessLayer;
    public SchemaPopulator(
        IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer;
    }

    public async Task<IReadOnlyCollection<UpdateError>> Populate()
    {
        var errors = new List<UpdateError>();
        var schemaById = this.LoadEmbeddedSchemas(errors);
        var changes = this.LoadEntityChanges(
            schemaById,
            errors);

        var updateResult = await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest(
                new UpdateMetadata(new Markdown("Populate built-in schemas and entities.")),
                changes));

        foreach (var entityResult in updateResult.EntityResults)
        {
            errors.AddRange(entityResult.Errors);
        }

        return errors;
    }

    private Dictionary<string, JsonObject> LoadEmbeddedSchemas(
        ICollection<UpdateError> errors)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var schemaById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(".JsonSchemas.", StringComparison.Ordinal)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                errors.Add(new UpdateError($"Schema resource '{resourceName}' could not be read.", null));
                continue;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            JsonObject? schemaObject;
            try
            {
                schemaObject = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException exception)
            {
                errors.Add(new UpdateError(exception.Message, null));
                continue;
            }

            if (schemaObject is null)
            {
                errors.Add(new UpdateError($"Schema resource '{resourceName}' did not parse to an object.", null));
                continue;
            }

            if (!schemaObject.TryGetPropertyValue("$id", out var idNode)
                || idNode is not JsonValue idValue
                || !idValue.TryGetValue<string>(out var schemaId)
                || string.IsNullOrWhiteSpace(schemaId))
            {
                errors.Add(new UpdateError($"Schema resource '{resourceName}' is missing a valid '$id'.", null));
                continue;
            }

            schemaById[schemaId] = (JsonObject)schemaObject.DeepClone();
        }

        return schemaById;
    }

    private IReadOnlyCollection<EntityChange> LoadEntityChanges(
        IReadOnlyDictionary<string, JsonObject> schemaById,
        ICollection<UpdateError> errors)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var entityChanges = new List<EntityChange>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(".JsonEntities.", StringComparison.Ordinal)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            JsonObject? entityObject;
            try
            {
                entityObject = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException exception)
            {
                errors.Add(new UpdateError(exception.Message, null));
                continue;
            }

            if (entityObject is null)
            {
                continue;
            }

            var populatedEntity = (JsonObject)this.ReplaceSchemaReferences(
                entityObject,
                schemaById,
                errors);
            var entityId = this.GetEntityId(populatedEntity);

            entityChanges.Add(
                new EntityChange(
                    entityId,
                    null,
                    populatedEntity,
                    MergeMode.Replace));
        }

        return entityChanges;
    }

    private JsonNode ReplaceSchemaReferences(
        JsonNode node,
        IReadOnlyDictionary<string, JsonObject> schemaById,
        ICollection<UpdateError> errors)
    {
        return node switch
        {
            JsonObject jsonObject => this.ReplaceSchemaReferences(jsonObject, schemaById, errors),
            JsonArray jsonArray => this.ReplaceSchemaReferences(jsonArray, schemaById, errors),
            _ => node.DeepClone(),
        };
    }

    private JsonObject ReplaceSchemaReferences(
        JsonObject jsonObject,
        IReadOnlyDictionary<string, JsonObject> schemaById,
        ICollection<UpdateError> errors)
    {
        var clone = (JsonObject)jsonObject.DeepClone();
        foreach (var property in clone.ToList())
        {
            clone[property.Key] = this.ReplacePropertyValue(property.Key, property.Value, schemaById, errors);
        }

        return clone;
    }

    private JsonArray ReplaceSchemaReferences(
        JsonArray jsonArray,
        IReadOnlyDictionary<string, JsonObject> schemaById,
        ICollection<UpdateError> errors)
    {
        var clone = (JsonArray)jsonArray.DeepClone();
        for (var index = 0; index < clone.Count; index++)
        {
            clone[index] = clone[index] is null
                ? null
                : this.ReplaceSchemaReferences(clone[index]!, schemaById, errors);
        }

        return clone;
    }

    private JsonNode? ReplacePropertyValue(
        string propertyName,
        JsonNode? value,
        IReadOnlyDictionary<string, JsonObject> schemaById,
        ICollection<UpdateError> errors)
    {
        if (propertyName == "schema"
            && value is JsonValue schemaValue
            && schemaValue.TryGetValue<string>(out var schemaId)
            && schemaById.TryGetValue(schemaId, out var schemaObject))
        {
            return schemaObject.DeepClone();
        }

        if (propertyName == "schema"
            && value is JsonValue unresolvedSchemaValue
            && unresolvedSchemaValue.TryGetValue<string>(out var unresolvedSchemaId))
        {
            errors.Add(
                new UpdateError(
                    $"Schema '{unresolvedSchemaId}' could not be resolved.",
                    null));
        }

        return value is null ? null : this.ReplaceSchemaReferences(value, schemaById, errors);
    }

    private EntityId? GetEntityId(
        JsonObject entityObject)
    {
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
