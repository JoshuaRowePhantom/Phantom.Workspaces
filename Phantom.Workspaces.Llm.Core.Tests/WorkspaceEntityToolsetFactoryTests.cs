using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class WorkspaceEntityToolsetFactoryTests
{
    [Fact]
    public async Task CreateToolsetAsync_WhenKindMatches_ReturnsWorkspaceEntityTools()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new WorkspaceEntityToolsetFactory(dataAccessLayer);

        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("workspace-entity"), new AgentServices());

        Assert.NotNull(toolset);
        var toolNames = (await toolset.ListToolsAsync())
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "workspace_entity_add",
                "workspace_entity_delete",
                "workspace_entity_get_by_id",
                "workspace_entity_get_by_name",
                "workspace_entity_replace",
            ],
            toolNames);
    }

    [Fact]
    public async Task ReplaceTool_WithoutConcurrencyTag_ReturnsValidationError()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = await GetToolAsync(dataAccessLayer, "workspace_entity_replace");

        var entityId = Guid.NewGuid().ToString();
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = entityId,
                ["data"] = JsonDocument.Parse("""{ "kind": "test" }""").RootElement.Clone(),
            }),
            CancellationToken.None);

        var textContent = Assert.IsType<TextContent>(result);
        Assert.Contains("concurrency-tag", textContent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAndReplace_WithCurrentConcurrencyTag_UpdatesEntity()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var addTool = await GetToolAsync(dataAccessLayer, "workspace_entity_add");
        var getByIdTool = await GetToolAsync(dataAccessLayer, "workspace_entity_get_by_id");
        var replaceTool = await GetToolAsync(dataAccessLayer, "workspace_entity_replace");

        var entityId = Guid.NewGuid().ToString();
        await addTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = entityId,
                ["data"] = JsonDocument.Parse("""{ "entity-type-names": [ "sample" ], "entity-name": [ "samples", "one" ], "value": "before" }""").RootElement.Clone(),
            }),
            CancellationToken.None);

        var getResult = await getByIdTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = entityId,
            }),
            CancellationToken.None);
        var getJson = ReadJsonFromTextContent(getResult);
        var concurrencyTag = getJson.GetProperty("entity").GetProperty("concurrencyTag").GetString();
        Assert.False(string.IsNullOrWhiteSpace(concurrencyTag));

        var replaceResult = await replaceTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = entityId,
                ["concurrency-tag"] = concurrencyTag,
                ["data"] = JsonDocument.Parse("""{ "entity-type-names": [ "sample" ], "entity-name": [ "samples", "one" ], "value": "after" }""").RootElement.Clone(),
            }),
            CancellationToken.None);
        var replaceJson = ReadJsonFromTextContent(replaceResult);

        var updateState = replaceJson.GetProperty("entityResults")[0].GetProperty("updateState").GetString();
        Assert.Equal("Updated", updateState);
    }

    [Fact]
    public async Task ReplaceTool_WithWrongConcurrencyTag_ReturnsNotMatched()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var addTool = await GetToolAsync(dataAccessLayer, "workspace_entity_add");
        var replaceTool = await GetToolAsync(dataAccessLayer, "workspace_entity_replace");

        var entityId = Guid.NewGuid().ToString();
        await addTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = entityId,
                ["data"] = JsonDocument.Parse("""{ "entity-type-names": [ "sample" ], "entity-name": [ "samples", "one" ], "value": "before" }""").RootElement.Clone(),
            }),
            CancellationToken.None);

        var replaceResult = await replaceTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = entityId,
                ["concurrency-tag"] = "wrong-concurrency-tag",
                ["data"] = JsonDocument.Parse("""{ "entity-type-names": [ "sample" ], "entity-name": [ "samples", "one" ], "value": "after" }""").RootElement.Clone(),
            }),
            CancellationToken.None);
        var replaceJson = ReadJsonFromTextContent(replaceResult);
        var entityResult = replaceJson.GetProperty("entityResults")[0];

        Assert.Equal("Failed", entityResult.GetProperty("updateState").GetString());
        Assert.Equal("NotMatched", entityResult.GetProperty("concurrencyMatchState").GetString());
    }

    private static JsonElement ReadJsonFromTextContent(object? result)
    {
        var textContent = Assert.IsType<TextContent>(result);
        using var document = JsonDocument.Parse(textContent.Text);
        return document.RootElement.Clone();
    }

    private static async Task<AIFunction> GetToolAsync(
        InMemoryDataAccessLayer dataAccessLayer,
        string toolName)
    {
        var factory = new WorkspaceEntityToolsetFactory(dataAccessLayer);
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("workspace-entity"), new AgentServices());
        Assert.NotNull(toolset);
        var tool = (await toolset.ListToolsAsync()).Single(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return Assert.IsAssignableFrom<AIFunction>(tool);
    }

    private static Tool CreateCustomTool(string kind)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "workspace-entity-toolset-test-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                {
                  "kind": "{{kind}}"
                }
              ]
            }
            """);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        return Assert.IsType<CustomTool>(Assert.Single(promptAgent.Tools!));
    }
}
