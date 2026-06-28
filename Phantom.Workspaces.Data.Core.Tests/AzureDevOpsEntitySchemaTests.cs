using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class AzureDevOpsEntitySchemaTests
{
    private static readonly Assembly DataCoreAssembly = Assembly.GetAssembly(typeof(SchemaPopulator))!;

    [Fact]
    public void AzureDevOpsOrganization_EmbeddedSchema_ComposesOrganization()
    {
        using var document = LoadEmbeddedSchema("azure-devops-organization.json");

        Assert.True(document.RootElement.TryGetProperty("allOf", out var allOf));
        Assert.Contains(allOf.EnumerateArray(), item =>
            item.TryGetProperty("$ref", out var refVal) && refVal.GetString() == "organization.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("azure-devops-organization", contains.GetProperty("const").GetString());
    }

    [Fact]
    public void AzureDevOpsProject_EmbeddedSchema_ComposesRepository()
    {
        using var document = LoadEmbeddedSchema("azure-devops-project.json");

        Assert.True(document.RootElement.TryGetProperty("allOf", out var allOf));
        Assert.Contains(allOf.EnumerateArray(), item =>
            item.TryGetProperty("$ref", out var refVal) && refVal.GetString() == "repository.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("azure-devops-project", contains.GetProperty("const").GetString());
    }

    [Fact]
    public void AzureDevOpsWorkItem_EmbeddedSchema_ComposesWorkItem()
    {
        using var document = LoadEmbeddedSchema("azure-devops-work-item.json");

        Assert.True(document.RootElement.TryGetProperty("allOf", out var allOf));
        Assert.Contains(allOf.EnumerateArray(), item =>
            item.TryGetProperty("$ref", out var refVal) && refVal.GetString() == "work-item.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("azure-devops-work-item", contains.GetProperty("const").GetString());
    }

    [Fact]
    public void AzureDevOpsRepository_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("azure-devops-repository.json");

        Assert.True(document.RootElement.TryGetProperty("allOf", out var allOf));
        Assert.Contains(allOf.EnumerateArray(), item =>
            item.TryGetProperty("$ref", out var refVal) && refVal.GetString() == "git-repository.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("azure-devops-repository", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("repository-id", out _));
        Assert.True(properties.TryGetProperty("project", out _));
    }

    [Fact]
    public void AzureDevOpsPullRequest_EmbeddedSchema_ContainsExpectedProperties()
    {
        using var document = LoadEmbeddedSchema("azure-devops-pull-request.json");

        Assert.True(document.RootElement.TryGetProperty("allOf", out var allOf));
        Assert.Contains(allOf.EnumerateArray(), item =>
            item.TryGetProperty("$ref", out var refVal) && refVal.GetString() == "git-pull-request.json");

        Assert.True(document.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("entity-types", out var entityTypes));
        Assert.True(entityTypes.TryGetProperty("contains", out var contains));
        Assert.Equal("azure-devops-pull-request", contains.GetProperty("const").GetString());
        Assert.True(properties.TryGetProperty("pull-request-id", out _));
        Assert.True(properties.TryGetProperty("is-draft", out _));
        Assert.True(properties.TryGetProperty("author", out _));
        Assert.True(properties.TryGetProperty("merge-status", out _));
    }

    [Fact]
    public async Task Populate_IncludesAzureDevOpsOrganizationEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "azure-devops-organization"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesAzureDevOpsProjectEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "azure-devops-project"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesAzureDevOpsWorkItemEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "azure-devops-work-item"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesAzureDevOpsRepositoryEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "azure-devops-repository"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task Populate_IncludesAzureDevOpsPullRequestEntityType()
    {
        var seededNames = await GetSeededNamesAsync();
        Assert.Contains(seededNames, n => n.SequenceEqual(["entity-types", "azure-devops-pull-request"], StringComparer.Ordinal));
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
