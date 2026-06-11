using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm.Echo;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class WorkspaceEntityToolsetFactoryTests
{
    [Fact]
    public async Task CreateToolsetAsync_WhenKindMatches_ReturnsWorkspaceEntityTools()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = ToolsetFactory.CreateWorkspaceEntityToolsetFactory(dataAccessLayer);

        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("workspace-entity"), new AgentServices());

        var toolNames = (await GetToolsAsync(Assert.IsType<WorkspaceEntityContextProvider>(toolset)))
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
    public async Task ProvideAIContextAsync_LoadsInstructionsFromGlobalNote()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await StoreGlobalInstructionNoteAsync(
            dataAccessLayer,
            ["documentation", "entity-workspace-agent-tool-instructions"],
            "Retrieve [\"documentation\", \"entity-workspace-agent-tool-instruction-details\"] first.");
        var provider = new WorkspaceEntityContextProvider(dataAccessLayer);
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        var context = await AIContextProviderToolReader.GetContextAsync(provider, agent, session, CancellationToken.None);

        Assert.NotNull(context.Instructions);
        Assert.Contains("entity-workspace-agent-tool-instruction-details", context.Instructions, StringComparison.Ordinal);
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
    public async Task AddTool_WithoutEntityId_GeneratesEntityIdAndReturnsIt()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var addTool = await GetToolAsync(dataAccessLayer, "workspace_entity_add");
        var getByIdTool = await GetToolAsync(dataAccessLayer, "workspace_entity_get_by_id");

        var addResult = await addTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["data"] = JsonDocument.Parse("""{ "entity-type-names": [ "sample" ], "entity-name": [ "samples", "generated" ], "value": "created" }""").RootElement.Clone(),
            }),
            CancellationToken.None);
        var addJson = ReadJsonFromTextContent(addResult);
        var generatedEntityId = addJson.GetProperty("entityId").GetGuid();
        var updateState = addJson.GetProperty("update").GetProperty("entityResults")[0].GetProperty("updateState").GetString();
        Assert.Equal("Added", updateState);

        var getResult = await getByIdTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["entity-id"] = generatedEntityId.ToString("D"),
            }),
            CancellationToken.None);
        var getJson = ReadJsonFromTextContent(getResult);
        Assert.Equal(generatedEntityId, getJson.GetProperty("entity").GetProperty("entityId").GetGuid());
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
        var factory = new WorkspaceEntityContextProvider(dataAccessLayer);
        var tool = (await GetToolsAsync(factory)).Single(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
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

    private static async Task<AITool[]> GetToolsAsync(WorkspaceEntityContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }

    private static async Task StoreGlobalInstructionNoteAsync(
        IDataAccessLayer dataAccessLayer,
        string[] entityName,
        string markdownText)
    {
        using var jsonDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{Guid.NewGuid():D}}",
              "entity-types": ["note"],
              "names": [{{JsonSerializer.Serialize(entityName)}}],
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": {
                    "text": {{JsonSerializer.Serialize(markdownText)}}
                  }
                }
              }
            }
            """);
        var entityData = jsonDocument.RootElement.Clone();

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Seed global workspace entity instruction note.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(entityData.GetProperty("entity-id").GetString()!),
                        Data = entityData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            CancellationToken.None);

        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }
}
