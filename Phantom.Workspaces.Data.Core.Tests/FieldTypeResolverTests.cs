using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class FieldTypeResolverTests
{
    [Fact]
    public async Task ResolveFieldTypeAsync_ResolvesMimeAttachmentAndDefaultMimeType()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var noteEntity = await GetEntityByNameAsync(dataAccessLayer, ["documentation", "getting-started"]);
        Assert.NotNull(noteEntity);
        Assert.True(noteEntity.Value.TryGetProperty("content", out var contentValue));

        var resolver = new FieldTypeResolver(new SchemaAccessor(dataAccessLayer));
        var resolvedType = await resolver.ResolveFieldTypeAsync(noteEntity.Value, ["content"], contentValue);

        Assert.Equal("mime-attachment", resolvedType.TypeName);
        Assert.Equal("text/markdown", resolvedType.DefaultMimeType);
    }

    [Fact]
    public async Task ResolveFieldTypeAsync_ResolvesLocalString()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var noteEntity = await GetEntityByNameAsync(dataAccessLayer, ["documentation", "getting-started"]);
        Assert.NotNull(noteEntity);
        Assert.True(noteEntity.Value.TryGetProperty("display-name", out var displayNameValue));

        var resolver = new FieldTypeResolver(new SchemaAccessor(dataAccessLayer));
        var resolvedType = await resolver.ResolveFieldTypeAsync(noteEntity.Value, ["display-name"], displayNameValue);

        Assert.Equal("local-string", resolvedType.TypeName);
    }

    [Fact]
    public async Task EnumerateObjectFieldNamesAsync_IncludesSchemaDefinedAndExistingFields()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        var noteEntity = await GetEntityByNameAsync(dataAccessLayer, ["documentation", "getting-started"]);
        Assert.NotNull(noteEntity);

        var resolver = new FieldTypeResolver(new SchemaAccessor(dataAccessLayer));
        var fieldNames = await resolver.EnumerateObjectFieldNamesAsync(noteEntity.Value, Array.Empty<string>(), noteEntity.Value);

        Assert.Contains("title", fieldNames);
        Assert.Contains("content", fieldNames);
        Assert.Contains("entity-id", fieldNames);
    }

    [Fact]
    public async Task ResolveFieldTypeAsync_WhenSchemaPropertyCannotBeResolved_FallsBackToJsonType()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "31554ca4-f952-4f4e-a62a-e517844f9bb2",
              "names": [["tests","unknown-field-type"]],
              "custom-number": 123
            }
            """);

        var resolver = new FieldTypeResolver(new SchemaAccessor(dataAccessLayer));
        var customNumber = document.RootElement.GetProperty("custom-number");
        var resolvedType = await resolver.ResolveFieldTypeAsync(document.RootElement, ["custom-number"], customNumber);

        Assert.Equal("int", resolvedType.TypeName);
    }

    [Fact]
    public async Task ResolveFieldTypeAsync_PopulatesFieldStatusFromTaskStatusAnnotation()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "31554ca4-f952-4f4e-a62a-e517844f9bb2",
              "entity-types": ["entity", "task"],
              "status": "completed"
            }
            """);

        var resolver = new FieldTypeResolver(new SchemaAccessor(dataAccessLayer));
        var statusValue = document.RootElement.GetProperty("status");
        var resolvedType = await resolver.ResolveFieldTypeAsync(document.RootElement, ["status"], statusValue);

        Assert.NotNull(resolvedType.FieldStatus);
        Assert.Equal(["completed"], resolvedType.FieldStatus!.GoodStatusValues);
        Assert.Equal(["blocked", "cancelled"], resolvedType.FieldStatus.BadStatusValues);
    }

    [Fact]
    public async Task ResolveFieldTypeAsync_LeavesFieldStatusNullForUnannotatedField()
    {
        var dataAccessLayer = await CreatePopulatedDataAccessLayerAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "31554ca4-f952-4f4e-a62a-e517844f9bb2",
              "entity-types": ["entity", "task"],
              "assigned-to": "someone"
            }
            """);

        var resolver = new FieldTypeResolver(new SchemaAccessor(dataAccessLayer));
        var assignedTo = document.RootElement.GetProperty("assigned-to");
        var resolvedType = await resolver.ResolveFieldTypeAsync(document.RootElement, ["assigned-to"], assignedTo);

        Assert.Null(resolvedType.FieldStatus);
    }

    private static async Task<IDataAccessLayer> CreatePopulatedDataAccessLayerAsync()
    {
        var underlying = new InMemoryDataAccessLayer();
        var dataAccessLayer = new SchemaValidatingDataAccessLayer(new ReferentialIntegrityDataAccessLayer(underlying));
        var populator = new SchemaPopulator(dataAccessLayer);
        var errors = await populator.Populate();
        Assert.Empty(errors);
        return dataAccessLayer;
    }

    private static async Task<JsonElement?> GetEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        string[] nameComponents)
    {
#pragma warning disable CS0618
        var export = await dataAccessLayer.ExportAsync(new ExportRequest());
#pragma warning restore CS0618
        return export.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .FirstOrDefault(data =>
                data.TryGetProperty("names", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name =>
                    name.ValueKind == JsonValueKind.Array
                    && name.EnumerateArray().Select(static part => part.GetString()).SequenceEqual(nameComponents)));
    }
}

