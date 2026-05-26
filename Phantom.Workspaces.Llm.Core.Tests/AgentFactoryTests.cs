using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm.Echo;
using System.Runtime.InteropServices;

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
    public void ConfigureChatOptions_GitHubProvider_WithThinking_MapsReasoningEffort()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-agent",
              "model": {
                "id": "gpt-4.1-mini",
                "provider": "github",
                "apiType": "OpenAI",
                "connection": {
                  "kind": "key",
                  "apiKey": "${GITHUB_TOKEN}"
                },
                "options": {
                  "additionalProperties": {
                    "thinking": "high"
                  }
                }
              },
              "tools": []
            }
            """);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agent, chatOptions);

        Assert.NotNull(chatOptions.Reasoning);
        Assert.Equal(ReasoningEffort.High, chatOptions.Reasoning!.Effort);
    }

    [Fact]
    public void ConfigureChatOptions_WithoutThinking_DoesNotSetReasoningEffort()
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

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agent, chatOptions);

        Assert.Null(chatOptions.Reasoning);
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
        Assert.Null(chatOptions.Reasoning);
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
    public void CreateChatClient_TestProvider_ReturnsTestProviderClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        var (client, displayName) = AgentFactory.CreateChatClient(agent);

        Assert.IsType<TestProviderChatClient>(client);
        Assert.Equal("Test Chat Client", displayName);
    }

    [Fact]
    public void CreateChatClient_GitHubProvider_ReturnsOpenAiChatClient()
    {
        var original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var agent = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "github-models-agent",
                  "model": {
                    "id": "gpt-4.1-mini",
                    "provider": "github",
                    "apiType": "OpenAI",
                    "connection": {
                      "kind": "key",
                      "endpoint": "https://models.github.ai/inference",
                      "apiKey": "${GITHUB_TOKEN}"
                    }
                  },
                  "tools": []
                }
                """);

            var (client, displayName) = AgentFactory.CreateChatClient(agent);

            Assert.NotNull(client);
            Assert.Equal("GitHub Models (gpt-4.1-mini at https://models.github.ai/inference)", displayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }

    [Fact]
    public void CreateChatClient_GitHubProvider_WithNoEndpoint_UsesDefaultEndpoint()
    {
        var original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var agent = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "github-models-agent",
                  "model": {
                    "id": "gpt-4.1-mini",
                    "provider": "github",
                    "apiType": "OpenAI",
                    "connection": {
                      "kind": "key",
                      "apiKey": "${GITHUB_TOKEN}"
                    }
                  },
                  "tools": []
                }
                """);

            var (client, displayName) = AgentFactory.CreateChatClient(agent);

            Assert.NotNull(client);
            Assert.Equal("GitHub Models (gpt-4.1-mini at https://models.github.ai/inference)", displayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }

    [Fact]
    public void CreateChatClient_GitHubProvider_WithMissingEnv_UsesGhAuthToken()
    {
        var originalToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var tempDir = Path.Combine(Path.GetTempPath(), $"gh-stub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var ghToken = "token-from-gh-cli";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.WriteAllText(
                Path.Combine(tempDir, "gh.cmd"),
                $"@echo off{Environment.NewLine}echo {ghToken}{Environment.NewLine}exit /b 0{Environment.NewLine}");
        }
        else
        {
            var ghPath = Path.Combine(tempDir, "gh");
            File.WriteAllText(
                ghPath,
                $"#!/usr/bin/env sh{Environment.NewLine}echo {ghToken}{Environment.NewLine}");
            System.Diagnostics.Process.Start("chmod", $"+x \"{ghPath}\"")!.WaitForExit();
        }

        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("PATH", $"{tempDir}{Path.PathSeparator}{originalPath}");

        try
        {
            var agent = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "github-models-agent",
                  "model": {
                    "id": "gpt-4.1-mini",
                    "provider": "github",
                    "apiType": "OpenAI",
                    "connection": {
                      "kind": "key",
                      "apiKey": "${GITHUB_TOKEN}"
                    }
                  },
                  "tools": []
                }
                """);

            var (client, displayName) = AgentFactory.CreateChatClient(agent);

            Assert.NotNull(client);
            Assert.Equal("GitHub Models (gpt-4.1-mini at https://models.github.ai/inference)", displayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalToken);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAgentChat_EchoProvider_ReturnsRunningChat()
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

        await using var chat = AgentFactory.CreateAgentChat(agent);
        Assert.NotNull(chat);
        Assert.Equal("Echo Chat Client", chat.DisplayName);
    }

    [Fact]
    public void CreateAgentChat_LogChatWithoutLoggerFactory_Throws()
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

        var ex = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateAgentChat(agent, new AgentServices { LogChat = true }));
        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAgentChat_LogHttpRequestsWithoutLoggerFactory_Throws()
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

        var ex = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateAgentChat(agent, new AgentServices { LogHttpRequests = true }));
        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChat_LogChatWithLoggerFactory_ReturnsChat()
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

        await using var chat = AgentFactory.CreateAgentChat(agent, services);
        Assert.NotNull(chat);
        Assert.Equal("Echo Chat Client", chat.DisplayName);
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

    [Fact]
    public async Task CreateAgentChatAsync_EchoProvider_ReturnsRunningChat()
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

        await using var chat = await AgentFactory.CreateAgentChatAsync(agent);

        Assert.NotNull(chat);
        Assert.Equal("Echo Chat Client", chat.DisplayName);
    }

    [Fact]
    public async Task CreateAgentChatAsync_LogChatWithoutLoggerFactory_Throws()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentFactory.CreateAgentChatAsync(agent, new AgentServices { LogChat = true }));

        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }
}
