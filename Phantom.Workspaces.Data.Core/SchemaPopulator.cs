using System.Reflection;
using System.Text.Json;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Populates the initial built-in schema entities into a data access layer.
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
        var changes = this.LoadEntityChanges(errors);

        var updateResult = await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Populate built-in schema entities.",
                    },
                },
                Changes = changes,
            });

        foreach (var entityResult in updateResult.EntityResults)
        {
            errors.AddRange(entityResult.Errors);
        }

        return errors;
    }

    private IReadOnlyCollection<EntityChange> LoadEntityChanges(
        ICollection<UpdateError> errors)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var entityChanges = new List<EntityChange>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith("Phantom.Workspaces.Data.JsonSchemas.", StringComparison.Ordinal)
                && !resourceName.StartsWith("Phantom.Workspaces.Data.JsonEntities.", StringComparison.Ordinal)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                errors.Add(
                    new UpdateError
                    {
                        Message = $"Entity resource '{resourceName}' could not be read.",
                    });
                continue;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException exception)
            {
                errors.Add(
                    new UpdateError
                    {
                        Message = exception.Message,
                    });
                continue;
            }

            using (document)
            {
                var entityElement = document.RootElement.Clone();
                var entityId = this.GetEntityId(entityElement);
                entityChanges.Add(
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = entityElement,
                        EntityChangeMode = EntityChangeMode.Replace,
                    });
            }
        }

        return entityChanges;
    }

    private EntityId? GetEntityId(
        JsonElement entityObject)
    {
        if (!entityObject.TryGetProperty("entity-id", out var entityIdElement)
            || entityIdElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entityIdElement.GetString(), out var entityGuid))
        {
            return null;
        }

        return new EntityId(entityGuid);
    }
}
