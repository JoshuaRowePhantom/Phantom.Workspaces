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
        var markdownResourcesByPath = this.GetMarkdownResourcesByPath(assembly);
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
                var entityElement = this.MaterializeMarkdownAttachments(
                    document.RootElement,
                    resourceName,
                    assembly,
                    markdownResourcesByPath,
                    errors);
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

    private JsonElement MaterializeMarkdownAttachments(
        JsonElement element,
        string sourceResourceName,
        Assembly assembly,
        IReadOnlyDictionary<string, string> markdownResourcesByPath,
        ICollection<UpdateError> errors)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        this.WriteMaterializedElement(element, sourceResourceName, assembly, markdownResourcesByPath, errors, writer);
        writer.Flush();
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private void WriteMaterializedElement(
        JsonElement element,
        string sourceResourceName,
        Assembly assembly,
        IReadOnlyDictionary<string, string> markdownResourcesByPath,
        ICollection<UpdateError> errors,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                this.WriteMaterializedObject(element, sourceResourceName, assembly, markdownResourcesByPath, errors, writer);
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    this.WriteMaterializedElement(item, sourceResourceName, assembly, markdownResourcesByPath, errors, writer);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private void WriteMaterializedObject(
        JsonElement element,
        string sourceResourceName,
        Assembly assembly,
        IReadOnlyDictionary<string, string> markdownResourcesByPath,
        ICollection<UpdateError> errors,
        Utf8JsonWriter writer)
    {
        var markdownText = string.Empty;
        var shouldInjectMarkdownText =
            this.TryReadMarkdownUrl(element, out var markdownUrl)
            && !this.HasInlineTextContent(element)
            && this.TryLoadEmbeddedMarkdownText(markdownUrl, sourceResourceName, assembly, markdownResourcesByPath, errors, out markdownText);

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            this.WriteMaterializedElement(property.Value, sourceResourceName, assembly, markdownResourcesByPath, errors, writer);
        }

        if (shouldInjectMarkdownText)
        {
            writer.WritePropertyName("content");
            writer.WriteStartObject();
            writer.WriteString("text", markdownText);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private bool TryReadMarkdownUrl(
        JsonElement element,
        out string markdownUrl)
    {
        markdownUrl = string.Empty;
        if (!element.TryGetProperty("mime-type", out var mimeType)
            || mimeType.ValueKind != JsonValueKind.String
            || !string.Equals(mimeType.GetString(), "text/markdown", StringComparison.Ordinal))
        {
            return false;
        }

        if (!element.TryGetProperty("url", out var url)
            || url.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(url.GetString()))
        {
            return false;
        }

        markdownUrl = url.GetString()!;
        return true;
    }

    private bool HasInlineTextContent(
        JsonElement element)
    {
        return element.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String;
    }

    private bool TryLoadEmbeddedMarkdownText(
        string markdownUrl,
        string sourceResourceName,
        Assembly assembly,
        IReadOnlyDictionary<string, string> markdownResourcesByPath,
        ICollection<UpdateError> errors,
        out string markdownText)
    {
        markdownText = string.Empty;
        var normalizedPath = markdownUrl.Replace('\\', '/');
        if (!normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!markdownResourcesByPath.TryGetValue(normalizedPath, out var markdownResourceName))
        {
            return false;
        }

        using var markdownStream = assembly.GetManifestResourceStream(markdownResourceName);
        if (markdownStream is null)
        {
            return false;
        }

        using var markdownReader = new StreamReader(markdownStream);
        markdownText = markdownReader.ReadToEnd();
        return true;
    }

    private IReadOnlyDictionary<string, string> GetMarkdownResourcesByPath(
        Assembly assembly)
    {
        const string jsonEntitiesPrefix = "Phantom.Workspaces.Data.JsonEntities.";
        var markdownResourcesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(jsonEntitiesPrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativeName = resourceName[jsonEntitiesPrefix.Length..];
            var relativeWithoutExtension = relativeName[..^3];
            var logicalPath = $"{relativeWithoutExtension.Replace('.', '/')}.md";
            markdownResourcesByPath[logicalPath] = resourceName;
        }

        return markdownResourcesByPath;
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
