using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class GitEntitySchemaTests
{
    private static readonly Assembly DataCoreAssembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;

    [Fact]
    public void GitRepository_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("git-repository.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("git-repository", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("clone-url", out _));
        Assert.True(properties.TryGetProperty("ssh-url", out _));
    }

    [Fact]
    public void GitPullRequest_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("git-pull-request.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("git-pull-request", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("source-branch", out _));
        Assert.True(properties.TryGetProperty("target-branch", out _));
        Assert.True(properties.TryGetProperty("source-commit", out _));
        Assert.True(properties.TryGetProperty("merge-commit", out _));
        Assert.True(properties.TryGetProperty("repository", out _));
    }

    [Fact]
    public void GitWorkItem_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("git-work-item.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("git-work-item", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("repository", out _));
        Assert.True(properties.TryGetProperty("related-pull-requests", out _));
    }

    [Fact]
    public async Task Populate_IncludesGitRepositoryEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "git-repository"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitPullRequestEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "git-pull-request"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitWorkItemEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "git-work-item"], StringComparer.Ordinal));
    }

    private static JsonDocument LoadEmbeddedSchema(string fileName)
    {
        var resourceName = $"Phantom.Workspaces.Data.JsonSchemas.{fileName}";
        using var stream = DataCoreAssembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream!);
    }

    private static async Task<IReadOnlyCollection<string[]>> GetSeededNamesAsync()
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
}
