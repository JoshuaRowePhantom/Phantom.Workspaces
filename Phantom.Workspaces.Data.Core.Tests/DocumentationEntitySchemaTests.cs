using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class DocumentationEntitySchemaTests
{
    private static async Task<JsonElement[]> GetPopulatedEntitiesAsync()
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
            .ToArray();
    }

    private static JsonElement? FindEntityByTypeName(JsonElement[] entities, string entityTypeName) =>
        entities.FirstOrDefault(entity =>
            entity.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.EnumerateArray().Any(name =>
                name.ValueKind == JsonValueKind.Array
                && name.EnumerateArray()
                       .Select(static part => part.GetString())
                       .SequenceEqual(["entity-types", entityTypeName])));

    private static void AssertDocumentationMaterialized(JsonElement entity, string expectedHeading)
    {
        Assert.True(
            entity.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("default", out var defaultContent)
            && defaultContent.ValueKind == JsonValueKind.Object
            && defaultContent.TryGetProperty("mime-type", out var mimeType)
            && mimeType.ValueKind == JsonValueKind.String
            && string.Equals(mimeType.GetString(), "text/markdown", StringComparison.Ordinal)
            && !defaultContent.TryGetProperty("url", out _)
            && defaultContent.TryGetProperty("content", out var inlineContent)
            && inlineContent.ValueKind == JsonValueKind.Object
            && inlineContent.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            && text.GetString()!.Contains(expectedHeading, StringComparison.Ordinal),
            $"Documentation for '{expectedHeading}' was not materialized into inline markdown content");
    }

    [Fact]
    public async Task Populate_IncludesOrganizationDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "organization");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Organization Schema");
    }

    [Fact]
    public async Task Populate_IncludesRepositoryDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "repository");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Repository Schema");
    }

    [Fact]
    public async Task Populate_IncludesWorkItemDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "work-item");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Work Item Schema");
    }

    [Fact]
    public async Task Populate_IncludesPullRequestDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "pull-request");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Pull Request Schema");
    }

    [Fact]
    public async Task Populate_IncludesGitRepositoryDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "git-repository");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Git Repository Schema");
    }

    [Fact]
    public async Task Populate_IncludesGitPullRequestDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "git-pull-request");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Git Pull Request Schema");
    }

    [Fact]
    public async Task Populate_IncludesGitWorkItemDocumentation_WithMaterializedMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByTypeName(entities, "git-work-item");
        Assert.NotNull(entity);
        AssertDocumentationMaterialized(entity.Value, "# Git Work Item Schema");
    }
}
