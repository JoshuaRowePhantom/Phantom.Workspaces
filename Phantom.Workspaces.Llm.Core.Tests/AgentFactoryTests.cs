using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class AgentFactoryTests
{
    [Fact]
    public void ConfigureChatOptions_SetsInstructionsAndAdditionalInstructions()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "instructions": "Base instructions",
              "additionalInstructions": "Extra instructions",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agent, chatOptions);

        Assert.Equal("Base instructions", chatOptions.Instructions);
        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.True(chatOptions.AdditionalProperties!.TryGetValue("additionalInstructions", out var additional));
        Assert.Equal("Extra instructions", additional);
        Assert.False(chatOptions.AdditionalProperties.ContainsKey("system_instructions"));
    }

    [Fact]
    public void ConfigureChatOptions_MapsThinkingToReasoningEffort()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo",
                "options": {
                  "additionalProperties": {
                    "thinking": "low"
                  }
                }
              },
              "tools": []
            }
            """);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agent, chatOptions);

        Assert.NotNull(chatOptions.Reasoning);
        Assert.Equal(ReasoningEffort.Low, chatOptions.Reasoning!.Effort);
    }

    [Fact]
    public void ConfigureChatOptions_NonPromptAgent_IgnoresInstructions()
    {
        var nonPromptAgent = AgentDefinition.FromJson(
            """
            {
              "kind": "workflow",
              "name": "workflow-agent",
              "steps": []
            }
            """);

        Assert.NotNull(nonPromptAgent);

        var chatOptions = new ChatOptions
        {
            Instructions = "existing",
        };

        AgentFactory.ConfigureChatOptions(nonPromptAgent!, chatOptions);

        Assert.Equal("existing", chatOptions.Instructions);
        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.True(chatOptions.AdditionalProperties!.ContainsKey("agent_definition"));
        Assert.False(chatOptions.AdditionalProperties!.ContainsKey("additionalInstructions"));
        Assert.NotNull(chatOptions.Reasoning);
        Assert.Equal(ReasoningEffort.High, chatOptions.Reasoning!.Effort);
    }

    [Fact]
    public void ExtractTools_NonPromptAgent_ReturnsEmpty()
    {
        var nonPromptAgent = AgentDefinition.FromJson(
            """
            {
              "kind": "workflow",
              "name": "workflow-agent",
              "steps": []
            }
            """);

        var tools = AgentFactory.ExtractTools(nonPromptAgent!);
        Assert.NotNull(tools);
        Assert.Empty(tools!);
    }

    [Fact]
    public void GetSystemInstructions_NonPromptAgent_ReturnsEmpty()
    {
        var nonPromptAgent = AgentDefinition.FromJson(
            """
            {
              "kind": "workflow",
              "name": "workflow-agent",
              "steps": []
            }
            """);

        var instructions = AgentFactory.GetSystemInstructions(nonPromptAgent!);
        Assert.Equal(string.Empty, instructions);
    }

    [Fact]
    public void GetModelId_NonPromptAgent_ReturnsNull()
    {
        var nonPromptAgent = AgentDefinition.FromJson(
            """
            {
              "kind": "workflow",
              "name": "workflow-agent",
              "steps": []
            }
            """);

        var modelId = AgentFactory.GetModelId(nonPromptAgent!);
        Assert.Null(modelId);
    }

    [Fact]
    public void CreateChatClient_EchoProvider_ReturnsEchoClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var (client, displayName) = AgentFactory.CreateChatClient(agent);

        Assert.IsType<EchoChatClient>(client);
        Assert.Equal("Echo Chat Client", displayName);
    }

    [Fact]
    public void CreateAgent_EchoProvider_ReturnsChatClientAgentAndEchoClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var created = AgentFactory.CreateAgent(agent);

        Assert.NotNull(created.Agent);
        Assert.IsType<ChatClientAgent>(created.Agent);
        Assert.IsType<EchoChatClient>(created.Client);
        Assert.Equal("Echo Chat Client", created.DisplayName);
    }

    [Fact]
    public void CreateAgent_LogChatWithoutLoggerFactory_Throws()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateAgent(agent, new AgentServices { LogChat = true }));
        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAgent_LogHttpRequestsWithoutLoggerFactory_Throws()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "ollama-agent",
              "model": {
                "id": "qwen3.6",
                "provider": "ollama",
                "apiType": "Ollama",
                "connection": {
                  "kind": "Anonymous",
                  "endpoint": "http://localhost:11434"
                }
              },
              "tools": []
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateAgent(agent, new AgentServices { LogHttpRequests = true }));
        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAgent_LogChatWithLoggerFactory_ReturnsAgent()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var services = new AgentServices
        {
            LogChat = true,
            LoggerFactory = NullLoggerFactory.Instance,
        };

        var created = AgentFactory.CreateAgent(agent, services);
        Assert.NotNull(created.Agent);
        Assert.NotNull(created.Client);
    }

    [Fact]
    public void CreateChatClient_OllamaWithHttpLogging_ReturnsClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "ollama-agent",
              "model": {
                "id": "qwen3.6",
                "provider": "ollama",
                "apiType": "Ollama",
                "connection": {
                  "kind": "Anonymous",
                  "endpoint": "http://localhost:11434"
                }
              },
              "tools": []
            }
            """);

        var services = new AgentServices
        {
            LogHttpRequests = true,
            LoggerFactory = NullLoggerFactory.Instance,
        };

        var (client, displayName) = AgentFactory.CreateChatClient(agent, services);
        Assert.NotNull(client);
        Assert.Equal("Ollama (qwen3.6 at http://localhost:11434)", displayName);
    }
}
