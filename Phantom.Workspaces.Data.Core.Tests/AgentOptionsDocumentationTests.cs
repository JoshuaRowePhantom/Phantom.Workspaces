using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class AgentOptionsDocumentationTests
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

    private static JsonElement? FindEntityByName(JsonElement[] entities, params string[] nameParts) =>
        entities.FirstOrDefault(entity =>
            entity.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.EnumerateArray().Any(name =>
                name.ValueKind == JsonValueKind.Array
                && name.EnumerateArray()
                       .Select(static part => part.GetString())
                       .SequenceEqual(nameParts)));

    private static void AssertNoteHasInlineMarkdown(JsonElement entity, string expectedHeading)
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
            $"Agent-options note '{expectedHeading}' was not materialized into inline markdown content");
    }

    [Fact]
    public async Task Populate_AgentOptionsOverview_HasInlineMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByName(entities, "documentation", "agent-options", "overview");
        Assert.NotNull(entity);
        AssertNoteHasInlineMarkdown(entity.Value, "# Agent Options — Overview");
    }

    [Fact]
    public async Task Populate_AgentOptionsProviders_HasInlineMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByName(entities, "documentation", "agent-options", "providers");
        Assert.NotNull(entity);
        AssertNoteHasInlineMarkdown(entity.Value, "# Agent Options — Providers");
    }

    [Fact]
    public async Task Populate_AgentOptionsModelOptions_HasInlineMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByName(entities, "documentation", "agent-options", "model-options");
        Assert.NotNull(entity);
        AssertNoteHasInlineMarkdown(entity.Value, "# Agent Options — Model Options");
    }

    [Fact]
    public async Task Populate_AgentOptionsTools_HasInlineMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByName(entities, "documentation", "agent-options", "tools");
        Assert.NotNull(entity);
        AssertNoteHasInlineMarkdown(entity.Value, "# Agent Options — Tools");
    }

    [Fact]
    public async Task Populate_AgentOptionsParameters_HasInlineMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByName(entities, "documentation", "agent-options", "parameters");
        Assert.NotNull(entity);
        AssertNoteHasInlineMarkdown(entity.Value, "# Agent Options — Parameters");
    }

    [Fact]
    public async Task Populate_AgentOptionsConnections_HasInlineMarkdown()
    {
        var entities = await GetPopulatedEntitiesAsync();
        var entity = FindEntityByName(entities, "documentation", "agent-options", "connections");
        Assert.NotNull(entity);
        AssertNoteHasInlineMarkdown(entity.Value, "# Agent Options — Connections");
    }
}
