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
                "workspaces_entity_generate_guid",
                "workspaces_entity_get",
                "workspaces_entity_update",
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
            "Use workspaces_entity_get and workspaces_entity_update.");
        var provider = new WorkspaceEntityContextProvider(dataAccessLayer);
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        var context = await AIContextProviderToolReader.GetContextAsync(provider, agent, session, CancellationToken.None);

        Assert.NotNull(context.Instructions);
        Assert.Contains("workspaces_entity_get", context.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspacesEntityGet_ReturnsStructuredJsonElement_NotDoubleEncoded()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var updateTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_update");
        var getTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_get");
        var entityId = Guid.NewGuid();

        await updateTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["update-metadata"] = JsonDocument.Parse("""{ "comment": { "text": "Seed entity" } }""").RootElement.Clone(),
                ["changes"] = JsonDocument.Parse(
                    $$"""
                    [
                      {
                        "entity-id": "{{entityId:D}}",
                        "entity-change-mode": "replace",
                        "data": {
                          "entity-types": ["entity", "sample"],
                          "names": [["samples", "one"]],
                          "display-name": "Sample Entity"
                        }
                      }
                    ]
                    """).RootElement.Clone(),
            }),
            CancellationToken.None);

        var result = await getTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["get-entity"] = JsonDocument.Parse($$"""[{"entity-id":"{{entityId:D}}"}]""").RootElement.Clone(),
            }),
            CancellationToken.None);

        // The tool result must be structured JSON, not a stringified-JSON text envelope.
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal(JsonValueKind.Array, element.GetProperty("batches").ValueKind);

        // Guard against the prior double-encoding: no {"type":"text","text":"{...}"} wrapper,
        // and the payload is not a JSON string that itself contains escaped JSON.
        Assert.False(element.TryGetProperty("type", out _));
        Assert.False(element.TryGetProperty("text", out _));
        var serialized = element.GetRawText();
        Assert.DoesNotContain("\"type\":\"text\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0022", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspacesEntityGet_WithPropertiesFilter_ReturnsRequestedFields()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var updateTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_update");
        var getTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_get");
        var entityId = Guid.NewGuid();

        await updateTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["update-metadata"] = JsonDocument.Parse("""{ "comment": { "text": "Seed entity" } }""").RootElement.Clone(),
                ["changes"] = JsonDocument.Parse(
                    $$"""
                    [
                      {
                        "entity-id": "{{entityId:D}}",
                        "entity-change-mode": "replace",
                        "data": {
                          "entity-types": ["entity", "sample"],
                          "names": [["samples", "one"]],
                          "display-name": "Sample Entity",
                          "content": {
                            "default": {
                              "content": {
                                "text": "hello"
                              }
                            }
                          }
                        }
                      }
                    ]
                    """).RootElement.Clone(),
            }),
            CancellationToken.None);

        var result = await getTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["get-entity"] = JsonDocument.Parse($$"""[{"entity-id":"{{entityId:D}}"}]""").RootElement.Clone(),
                ["properties"] = JsonDocument.Parse("""["display-name","content.default.content.text"]""").RootElement.Clone(),
            }),
            CancellationToken.None);
        var resultJson = ReadJsonResult(result);
        var data = resultJson
            .GetProperty("batches")[0]
            .GetProperty("entities")[0]
            .GetProperty("data");
        Assert.True(data.TryGetProperty("display-name", out _));
        Assert.True(data.GetProperty("content").GetProperty("default").GetProperty("content").TryGetProperty("text", out _));
        Assert.False(data.TryGetProperty("names", out _));
    }

    [Fact]
    public async Task WorkspacesEntityUpdate_ReplaceAndDelete_WithConcurrencyTag_Succeeds()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var updateTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_update");
        var getTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_get");
        var entityId = Guid.NewGuid();

        var addResult = await updateTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["update-metadata"] = JsonDocument.Parse("""{ "comment": { "text": "Add entity" } }""").RootElement.Clone(),
                ["changes"] = JsonDocument.Parse(
                    $$"""
                    [
                      {
                        "entity-id": "{{entityId:D}}",
                        "entity-change-mode": "replace",
                        "data": {
                          "entity-types": ["entity", "sample"],
                          "names": [["samples", "delete-me"]],
                          "value": "before"
                        }
                      }
                    ]
                    """).RootElement.Clone(),
            }),
            CancellationToken.None);
        var addJson = ReadJsonResult(addResult);
        Assert.Equal("Added", addJson.GetProperty("entityResults")[0].GetProperty("updateState").GetString());

        var getResult = await getTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["get-entity"] = JsonDocument.Parse($$"""[{"entity-id":"{{entityId:D}}"}]""").RootElement.Clone(),
            }),
            CancellationToken.None);
        var getJson = ReadJsonResult(getResult);
        var concurrencyTag = getJson.GetProperty("batches")[0].GetProperty("entities")[0].GetProperty("concurrencyTag").GetString();
        Assert.False(string.IsNullOrWhiteSpace(concurrencyTag));

        var deleteResult = await updateTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["update-metadata"] = JsonDocument.Parse("""{ "comment": { "text": "Delete entity" } }""").RootElement.Clone(),
                ["changes"] = JsonDocument.Parse(
                    $$"""
                    [
                      {
                        "entity-id": "{{entityId:D}}",
                        "concurrency-tag": "{{concurrencyTag}}",
                        "entity-change-mode": "replace",
                        "data": null
                      }
                    ]
                    """).RootElement.Clone(),
            }),
            CancellationToken.None);
        var deleteJson = ReadJsonResult(deleteResult);
        Assert.Equal("Removed", deleteJson.GetProperty("entityResults")[0].GetProperty("updateState").GetString());
    }

    [Fact]
    public async Task WorkspacesEntityUpdate_RelationshipWithoutReasonNote_IsRejectedAndNotWritten()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var updateTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_update");
        var getTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_get");
        var relationshipId = Guid.NewGuid();

        var result = await updateTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["update-metadata"] = JsonDocument.Parse("""{ "comment": { "text": "Create relationship" } }""").RootElement.Clone(),
                ["changes"] = JsonDocument.Parse(
                    $$"""
                    [
                      {
                        "entity-id": "{{relationshipId:D}}",
                        "entity-change-mode": "replace",
                        "data": {
                          "entity-types": ["entity", "assigned-to", "relationship"],
                          "participants": { "target": "{{Guid.NewGuid():D}}", "user": "{{Guid.NewGuid():D}}" }
                        }
                      }
                    ]
                    """).RootElement.Clone(),
            }),
            CancellationToken.None);

        var error = Assert.IsType<string>(result);
        Assert.Contains("note", error, StringComparison.Ordinal);

        // The relationship must not have been written when the reason note is missing.
        var getResult = await getTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["get-entity"] = JsonDocument.Parse($$"""[{"entity-id":"{{relationshipId:D}}"}]""").RootElement.Clone(),
            }),
            CancellationToken.None);
        var getJson = ReadJsonResult(getResult);
        Assert.Empty(getJson.GetProperty("batches")[0].GetProperty("entities").EnumerateArray());
    }

    [Fact]
    public async Task WorkspacesEntityUpdate_RelationshipWithReasonNote_Succeeds()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var updateTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_update");
        var relationshipId = Guid.NewGuid();

        var result = await updateTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["update-metadata"] = JsonDocument.Parse("""{ "comment": { "text": "Create relationship" } }""").RootElement.Clone(),
                ["changes"] = JsonDocument.Parse(
                    $$"""
                    [
                      {
                        "entity-id": "{{relationshipId:D}}",
                        "entity-change-mode": "replace",
                        "data": {
                          "entity-types": ["entity", "assigned-to", "relationship"],
                          "participants": { "target": "{{Guid.NewGuid():D}}", "user": "{{Guid.NewGuid():D}}" },
                          "note": "Task is assigned to the user per the project board."
                        }
                      }
                    ]
                    """).RootElement.Clone(),
            }),
            CancellationToken.None);

        var json = ReadJsonResult(result);
        Assert.Equal("Added", json.GetProperty("entityResults")[0].GetProperty("updateState").GetString());
    }

    [Fact]
    public async Task WorkspacesEntityGenerateGuid_ReturnsGuid()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var generateGuidTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_generate_guid");

        var result = await generateGuidTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);
        var json = ReadJsonResult(result);
        Assert.True(Guid.TryParse(json.GetProperty("entityId").GetString(), out _));
    }

    [Fact]
    public async Task WorkspacesEntityGet_WithJsonEncodedStringPayload_ReturnsValidationError()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var getTool = await GetToolAsync(dataAccessLayer, "workspaces_entity_get");

        var result = await getTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["get-entity"] = """[{"entity-name":["documentation","entity-workspace-agent-tool-instruction-details"]}]""",
                ["properties"] = """["content.default.content.text"]""",
            }),
            CancellationToken.None);

        var textResult = Assert.IsType<string>(result);
        Assert.Contains("requires a valid GetRequest payload", textResult, StringComparison.Ordinal);
    }

    private static JsonElement ReadJsonResult(object? result)
    {
        return Assert.IsType<JsonElement>(result);
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
              "entity-types": ["entity", "note"],
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
