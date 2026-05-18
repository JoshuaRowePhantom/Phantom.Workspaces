using System.Reflection;
using System.Text.Json;
using System.Linq;

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
        var rawChanges = this.LoadEntityChanges(errors).ToArray();
        var changes = await this.ApplyCurrentConcurrencyTagsAsync(rawChanges);

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

    private async Task<IReadOnlyCollection<EntityChange>> ApplyCurrentConcurrencyTagsAsync(
        IReadOnlyCollection<EntityChange> changes)
    {
        var entityIds = changes
            .Where(static change => change.EntityId is not null)
            .Select(static change => change.EntityId!.Value)
            .Distinct()
            .ToArray();
        if (entityIds.Length == 0)
        {
            return changes;
        }

        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = entityIds.Select(static entityId => new GetEntityRequest { EntityId = entityId }).ToArray(),
                Timestamps = [null],
            });
        var snapshotsById = getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .ToDictionary(static snapshot => snapshot.EntityId, static snapshot => snapshot);

        if (snapshotsById.Count == 0)
        {
            return changes;
        }

        return changes
            .Select(
                change => change.EntityId is not null
                    && snapshotsById.TryGetValue(change.EntityId.Value, out var currentSnapshot)
                    && currentSnapshot.ConcurrencyTag is not null
                    ? change with { ConcurrencyTag = currentSnapshot.ConcurrencyTag }
                    : change)
            .ToArray();
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
