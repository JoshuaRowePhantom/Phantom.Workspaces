using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class GitHubEntitySchemaTests
{
    private static readonly Assembly DataCoreAssembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;

    [Fact]
    public void GitHubOrganization_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("github-organization.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("github-organization", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("github-login", out _));
        Assert.True(properties.TryGetProperty("github-node-id", out _));
    }

    [Fact]
    public void GitHubRepository_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("github-repository.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("github-repository", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("github-repo-id", out _));
        Assert.True(properties.TryGetProperty("github-node-id", out _));
        Assert.True(properties.TryGetProperty("owner", out _));
        Assert.True(properties.TryGetProperty("is-fork", out _));
        Assert.True(properties.TryGetProperty("is-archived", out _));
    }

    [Fact]
    public void GitHubPullRequest_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("github-pull-request.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("github-pull-request", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("number", out _));
        Assert.True(properties.TryGetProperty("github-node-id", out _));
        Assert.True(properties.TryGetProperty("is-draft", out _));
        Assert.True(properties.TryGetProperty("author", out _));
    }

    [Fact]
    public void GitHubWorkItem_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("github-work-item.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("github-work-item", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("number", out _));
        Assert.True(properties.TryGetProperty("github-node-id", out _));
        Assert.True(properties.TryGetProperty("author", out _));
        Assert.True(properties.TryGetProperty("milestone", out _));
    }

    [Fact]
    public async Task Populate_IncludesGitHubOrganizationEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "github-organization"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitHubRepositoryEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "github-repository"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitHubPullRequestEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "github-pull-request"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesGitHubWorkItemEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "github-work-item"], StringComparer.Ordinal));
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
