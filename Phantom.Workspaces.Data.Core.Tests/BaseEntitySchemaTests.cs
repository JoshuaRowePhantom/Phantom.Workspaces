using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class BaseEntitySchemaTests
{
    private static async Task<string[][]> GetSeededNamesAsync()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var validatedDataAccessLayer = new SchemaValidatingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(inMemoryDataAccessLayer));
        var schemaPopulator = new SchemaPopulator(validatedDataAccessLayer);

        var errors = await schemaPopulator.Populate();
        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(e => $"{e.RelatedEntityId?.Value}: {e.Message}")));

        var exportResult = await inMemoryDataAccessLayer.ExportAsync(new ExportRequest());
        return exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Select(static entity => entity.Data)
            .OfType<JsonElement>()
            .Where(static data => data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            .SelectMany(static data => data.GetProperty("names").EnumerateArray())
            .Select(static name => name.TryReadEntityName())
            .Where(static name => name is not null)
            .Select(static name => name!.Value.Components)
            .ToArray();
    }

    [Fact]
    public async Task Populate_LoadsEmbeddedEntities_RegistersOrganizationEntityType()
    {
        var names = await GetSeededNamesAsync();
        Assert.Contains(names, static n => n.SequenceEqual(["entity-types", "organization"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_LoadsEmbeddedEntities_RegistersRepositoryEntityType()
    {
        var names = await GetSeededNamesAsync();
        Assert.Contains(names, static n => n.SequenceEqual(["entity-types", "repository"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_LoadsEmbeddedEntities_RegistersWorkItemEntityType()
    {
        var names = await GetSeededNamesAsync();
        Assert.Contains(names, static n => n.SequenceEqual(["entity-types", "work-item"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_LoadsEmbeddedEntities_RegistersPullRequestEntityType()
    {
        var names = await GetSeededNamesAsync();
        Assert.Contains(names, static n => n.SequenceEqual(["entity-types", "pull-request"], StringComparer.Ordinal));
    }
}
