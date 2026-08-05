using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Concurrent;
using System.Reflection;
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
    public void ConfigureChatOptions_MapsModelOptionsToChatOptions()
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
                  "temperature": 0.7,
                  "topP": 0.9,
                  "topK": 40,
                  "frequencyPenalty": 0.1,
                  "presencePenalty": 0.2,
                  "maxOutputTokens": 2048,
                  "additionalProperties": {
                    "num_ctx": 32768
                  }
                }
              },
              "tools": []
            }
            """);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agent, chatOptions);

        Assert.Equal(0.7f, chatOptions.Temperature);
        Assert.Equal(0.9f, chatOptions.TopP);
        Assert.Equal(0.1f, chatOptions.FrequencyPenalty);
        Assert.Equal(0.2f, chatOptions.PresencePenalty);
        Assert.Equal(2048, chatOptions.MaxOutputTokens);
        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.Equal(40, chatOptions.AdditionalProperties!["topK"]);
        Assert.Equal("32768", chatOptions.AdditionalProperties["num_ctx"]?.ToString());
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
                "provider": "github-models",
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
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-models-agent",
              "model": {
                "id": "gpt-4.1-mini",
                "provider": "github-models",
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

        var (client, displayName) = AgentFactory.CreateChatClient(
            agent,
            services: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        Assert.NotNull(client);
        Assert.Equal("GitHub Models (gpt-4.1-mini at https://models.github.ai/inference)", displayName);
    }

    [Fact]
    public void CreateChatClient_GitHubProvider_WithNoEndpoint_UsesDefaultEndpoint()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-models-agent",
              "model": {
                "id": "gpt-4.1-mini",
                "provider": "github-models",
                "apiType": "OpenAI",
                "connection": {
                  "kind": "key",
                  "apiKey": "${GITHUB_TOKEN}"
                }
              },
              "tools": []
            }
            """);

        var (client, displayName) = AgentFactory.CreateChatClient(
            agent,
            services: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        Assert.NotNull(client);
        Assert.Equal("GitHub Models (gpt-4.1-mini at https://models.github.ai/inference)", displayName);
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
                    "provider": "github-models",
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
    public void CreateChatClient_GitHubCopilotProvider_ReturnsCopilotSdkClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-copilot-agent",
              "model": {
                "id": "gpt-4.1-mini",
                "provider": "github-copilot",
                "apiType": "OpenAI",
                "connection": {
                  "kind": "key",
                  "apiKey": "${GITHUB_TOKEN}"
                }
              },
              "tools": []
            }
            """);

        var (client, displayName) = AgentFactory.CreateChatClient(
            agent,
            services: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        Assert.NotNull(client);
        Assert.IsType<CopilotSdkChatClient>(client);
        Assert.Equal("GitHub Copilot (gpt-4.1-mini)", displayName);
    }

    private sealed class RecordingAccountUpsertService : IGitHubAccountUpsertService
    {
        public Task UpsertForTokenAsync(string token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void AgentFactory_CopilotClientCreated_PassesAccountUpsertService()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-copilot-agent",
              "model": {
                "id": "gpt-5",
                "provider": "github-copilot",
                "apiType": "OpenAI"
              },
              "tools": []
            }
            """);

        var upsertService = new RecordingAccountUpsertService();
        var services = new AgentServices { AccountUpsertService = upsertService };

        var (client, _) = AgentFactory.CreateChatClient(agent, services);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(client);
        Assert.Same(upsertService, copilotClient.AccountUpsertService);
    }

    [Fact]
    public void CreateChatClient_GitHubCopilotProvider_WithoutConnection_UsesLoggedInUser()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-copilot-agent",
              "model": {
                "id": "gpt-5",
                "provider": "github-copilot",
                "apiType": "OpenAI"
              },
              "tools": []
            }
            """);

        var (client, displayName) = AgentFactory.CreateChatClient(agent);

        Assert.NotNull(client);
        Assert.IsType<CopilotSdkChatClient>(client);
        Assert.Equal("GitHub Copilot (gpt-5)", displayName);
    }

    [Fact]
    public void CreateChatClient_GitHubCopilotProvider_PassesModelOptionsToCopilotClient()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "github-copilot-agent",
              "model": {
                "id": "gpt-5",
                "provider": "github-copilot",
                "apiType": "OpenAI",
                "options": {
                  "additionalProperties": {
                    "working-directory": "/my/project"
                  }
                }
              },
              "tools": []
            }
            """);

        var (client, _) = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(client);
        var modelOptions = (ModelOptions?)typeof(CopilotSdkChatClient)
            .GetField("modelOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(copilotClient);

        Assert.NotNull(modelOptions);
        Assert.Equal("/my/project", modelOptions!.AdditionalProperties?["working-directory"]);
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

        await using var chat = await CreateChatAsync(agent);
        Assert.NotNull(chat);
        Assert.Equal("Echo Chat Client", chat.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(chat.AgentSessionId));
    }

    [Fact]
    public async Task CreateAgentChat_LogChatWithoutLoggerFactory_Throws()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CreateChatAsync(agent, new AgentServices { LogChat = true }));
        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChat_LogHttpRequestsWithoutLoggerFactory_Throws()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CreateChatAsync(agent, new AgentServices { LogHttpRequests = true }));
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

        await using var chat = await CreateChatAsync(agent, services);
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

        await using var chat = await CreateChatAsync(agent);

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
            () => CreateChatAsync(agent, new AgentServices { LogChat = true }));

        Assert.Contains("LoggerFactory is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChat_DefaultHistoryProvider_PublishesConversationHistory()
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

        await using var chat = await CreateChatAsync(agent);
        chat.EnqueueUserMessage("hello");
        await WaitForConditionAsync(chat.History, () => chat.History.Count >= 2, "history to include user and assistant items");

        Assert.True(chat.History.Count >= 2);
        Assert.Contains(chat.History, static item => item.Role == ChatRole.User);
        Assert.Contains(chat.History, static item => item.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task CreateAgentChat_ChatHistoryProvider_IsInvokedOnTurn()
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

        var store = new RecordingAgentPersistenceStore();
        var services = new AgentServices { AgentPersistenceStoreOverride = store };

        await using var chat = await CreateChatAsync(agent, services);
        chat.EnqueueUserMessage("hello");
        await store.WaitForStoreCallAsync();

        Assert.True(store.ReadCalls >= 1, "ReadMessagesAsync should have been called at least once");
        Assert.True(store.StoreCalls >= 1, "StoreAsync should have been called at least once");
    }

    [Fact]
    [Trait("Category", "SlowDocker")]
    public async Task CreateAgentChat_UsesAgentDefinitionChatHistoryTool()
    {
        var mongoConfig = new MongoDbChatHistoryProviderDefinition
        {
            MongoProvider = "container",
            DatabaseName = "test-db",
            CollectionName = "test-collection",
            ContainerName = "test-mongo",
            DataDirectory = "/tmp/mongo",
        };
        
        var mongoConfigJson = System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(mongoConfig.ToJson())
        );

        var agentJson = $$"""
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                {
                  "name": "chat-history",
                  "kind": "chat-history",
                  "options": {
                    "connection": {{mongoConfigJson}}
                  }
                }
              ]
            }
            """;

        var agent = AgentDefinitionLoader.LoadAgentFromJson(agentJson);

        // This should extract and create a MongoDB provider from the agent's chat-history tool
        await using var chat = await CreateChatAsync(agent);
        
        // Verify the chat was created successfully
        Assert.NotNull(chat);
        chat.EnqueueUserMessage("hello");
    }

    [Fact]
    public async Task CreateAgentChatAsync_RequestWithoutResolvableAgentDefinition_Throws()
    {
        var store = new RecordingAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = store,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest
                {
                    AgentSessionId = "unknown-session",
                    AgentServices = services,
                }));

        Assert.Contains("Agent definition could not be resolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChatAsync_WithSessionIdAndNoRestore_StoresUsingRequestedSessionId()
    {
        var store = new RecordingAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = store,
        };
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

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = "requested-session-id",
                AgentDefinition = agent,
                AgentServices = services,
            });
        chat.EnqueueUserMessage("hello");
        await store.WaitForStoreCallAsync();

        Assert.Equal("requested-session-id", chat.AgentSessionId);
        Assert.Contains("requested-session-id", store.StoredAgentSessionIds);
    }

    [Fact]
    public async Task CreateAgentChatAsync_WithSessionIdAndRestoredDefinition_CreatesChat()
    {
        var restoredDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "restored-echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);
        var store = new RecordingAgentPersistenceStore
        {
            RestoredAgent = new PersistedAgent
            {
                AgentSessionId = "restored-session-id",
                AgentDefinitionJson = BsonDocument.Parse(restoredDefinition.ToJson()),
            },
        };
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = store,
        };

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = "restored-session-id",
                AgentServices = services,
            });

        Assert.NotNull(chat);
        Assert.Equal("Echo Chat Client", chat.DisplayName);
        Assert.Equal("restored-session-id", chat.AgentSessionId);
        Assert.Equal(1, store.RestoreCalls);
    }

    [Fact]
    public async Task CreateAgentChatAsync_WithSessionIdAndAgentSchema_UsesProvidedDefinition()
    {
        var restoredDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "restored-test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);
        var providedDefinition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "provided-echo-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);
        var store = new RecordingAgentPersistenceStore
        {
            RestoredAgent = new PersistedAgent
            {
                AgentSessionId = "provided-session-id",
                AgentDefinitionJson = BsonDocument.Parse(restoredDefinition.ToJson()),
            },
        };
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = store,
        };

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = "provided-session-id",
                AgentDefinition = providedDefinition,
                AgentServices = services,
            });

        Assert.NotNull(chat);
        Assert.Equal("Echo Chat Client", chat.DisplayName);
        Assert.Equal("provided-session-id", chat.AgentSessionId);
        Assert.Equal(1, store.RestoreCalls);
    }

    [Fact]
    public async Task CreateAgentChatAsync_WithPersistedMessages_LoadsHistoryIntoChat()
    {
        var agentDefinition = CreateEchoPromptAgentDefinition();
        var agent = new ChatClientAgent(
            new EchoChatClient(),
            new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
            });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        var serializedSession = await agent.SerializeSessionAsync(session, cancellationToken: CancellationToken.None);
        var store = new InMemoryAgentPersistenceStore();
        var sessionId = "loaded-history-session-id";

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = sessionId,
                    AgentSessionJson = BsonDocument.Parse(serializedSession.GetRawText()),
                    AgentDefinitionJson = BsonDocument.Parse(agentDefinition.ToJson()),
                },
                NewMessages =
                [
                    new ChatMessage(ChatRole.User, "hello"),
                    new ChatMessage(ChatRole.Assistant, "world"),
                ],
            },
            CancellationToken.None);

        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = store,
        };

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentServices = services,
            });

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("hello", string.Concat(chat.History[0].Contents.OfType<TextContent>().Select(static content => content.Text)));
        Assert.Equal("world", string.Concat(chat.History[1].Contents.OfType<TextContent>().Select(static content => content.Text)));
    }

    [Fact]
    public async Task CreateAgentChatAsync_InMemoryStoreWithoutRequestedSessionId_GeneratesAgentSessionId()
    {
        var inMemoryAgentPersistenceStore = new InMemoryAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = inMemoryAgentPersistenceStore,
        };
        var agentDefinition = CreateEchoPromptAgentDefinition();

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentServices = services,
            });

        Assert.False(string.IsNullOrWhiteSpace(chat.AgentSessionId));
    }

    [Fact]
    public async Task CreateAgentChatAsync_InMemoryStoreWithRequestedSessionId_UsesRequestedSessionId()
    {
        var inMemoryAgentPersistenceStore = new InMemoryAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = inMemoryAgentPersistenceStore,
        };
        var agentDefinition = CreateEchoPromptAgentDefinition();

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = "in-memory-requested-session-id",
                AgentDefinition = agentDefinition,
                AgentServices = services,
            });
        chat.EnqueueUserMessage("hello");
        var restoreRequest = new RestoreRequest { AgentSessionId = "in-memory-requested-session-id" };
        await WaitForConditionAsync(
            chat.History,
            () => inMemoryAgentPersistenceStore.RestoreAsync(restoreRequest, CancellationToken.None).GetAwaiter().GetResult() is not null,
            "in-memory store to persist the requested session");

        Assert.Equal("in-memory-requested-session-id", chat.AgentSessionId);

        var restoredAgent = await inMemoryAgentPersistenceStore.RestoreAsync(
            restoreRequest,
            CancellationToken.None);
        Assert.NotNull(restoredAgent);
    }

    [Fact]
    public async Task CreateAgentChatAsync_InMemoryStoreWithUnknownSessionAndNoDefinition_Throws()
    {
        var inMemoryAgentPersistenceStore = new InMemoryAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = inMemoryAgentPersistenceStore,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentFactory.CreateAgentChatAsync(
                new CreateAgentChatRequest
                {
                    AgentSessionId = "in-memory-unknown-session-id",
                    AgentServices = services,
                }));

        Assert.Contains("Agent definition could not be resolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChatAsync_InMemoryStoreRestoresDefinitionFromStoredSessionId()
    {
        var inMemoryAgentPersistenceStore = new InMemoryAgentPersistenceStore();
        var services = new AgentServices
        {
            AgentPersistenceStoreOverride = inMemoryAgentPersistenceStore,
        };
        var agentDefinition = CreateEchoPromptAgentDefinition();

        await using (var firstChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = "in-memory-restored-session-id",
                AgentDefinition = agentDefinition,
                AgentServices = services,
            }))
        {
            firstChat.EnqueueUserMessage("persist this session");
            await WaitForConditionAsync(firstChat.History, () => firstChat.History.Count >= 2, "history to include assistant response before restore");
        }

        await using var restoredChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = "in-memory-restored-session-id",
                AgentServices = services,
            });

        Assert.NotNull(restoredChat);
        Assert.Equal("Echo Chat Client", restoredChat.DisplayName);
        Assert.Equal("in-memory-restored-session-id", restoredChat.AgentSessionId);
    }

    private static AgentDefinition CreateEchoPromptAgentDefinition()
    {
        return AgentDefinitionLoader.LoadAgentFromJson(
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
    }

    // -----------------------------------------------------------------------
    // PersistenceStoreFactory / CancellationToken threading tests (#698)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateAgentChat_DoesNotDeadlock_WhenPersistenceStoreCreationIsAsync()
    {
        // Regression guard: the old code used .GetAwaiter().GetResult() inside an async method.
        // When the factory's Task.Yield() posted its continuation back to a single-threaded
        // SynchronizationContext whose thread was already blocked, a deadlock occurred.
        // With await…ConfigureAwait(false) the SC thread is not blocked, so this test completes.
        var agent = LoadEchoAgentWithChatHistoryTool();
        using var ctx = new SingleThreadedSynchronizationContext();

        var chatTask = ctx.PostAsync(() =>
            AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = agent,
                PersistenceStoreFactory = async (def, ct) =>
                {
                    await Task.Yield();
                    return new InMemoryAgentPersistenceStore();
                },
            }));

        var winner = await Task.WhenAny(chatTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(winner == chatTask, "CreateAgentChatAsync deadlocked under a single-threaded SynchronizationContext.");
        await using var chat = await chatTask;
        Assert.NotNull(chat);
    }

    [Fact]
    public async Task CreateAgentChat_FallsBackToInMemory_WhenPersistenceStoreCreationThrows()
    {
        // The catch(Exception) in CreateAgentChatAsync swallows any factory exception
        // and falls back to InMemoryAgentPersistenceStore so the session stays alive.
        var agent = LoadEchoAgentWithChatHistoryTool();

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            PersistenceStoreFactory = (def, ct) =>
                ValueTask.FromException<IAgentPersistenceStore>(
                    new InvalidOperationException("Simulated store creation failure")),
        });

        Assert.NotNull(chat);
    }

    [Fact]
    public async Task CreateAgentChat_FallsBackToInMemory_WhenTokenCancelledDuringStoreCreation()
    {
        // OperationCanceledException is caught by the broad catch(Exception) in CreateAgentChatAsync
        // and falls back to InMemoryAgentPersistenceStore. This is the documented fall-back policy:
        // any failure during store creation (including cancellation) is swallowed to keep the session alive.
        var agent = LoadEchoAgentWithChatHistoryTool();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            CancellationToken = cts.Token,
            PersistenceStoreFactory = (def, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore());
            },
        });

        Assert.NotNull(chat);
    }

    [Fact]
    public async Task CreateAgentChat_PassesAmbientCancellationToken_ToStoreFactory()
    {
        var agent = LoadEchoAgentWithChatHistoryTool();
        using var cts = new CancellationTokenSource();
        CancellationToken capturedToken = default;

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            CancellationToken = cts.Token,
            PersistenceStoreFactory = (def, ct) =>
            {
                capturedToken = ct;
                return ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore());
            },
        });

        Assert.Equal(cts.Token, capturedToken);
    }

    private static AgentDefinition LoadEchoAgentWithChatHistoryTool() =>
        AgentDefinitionLoader.LoadAgentFromJson("""
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [
                {
                  "name": "chat-history",
                  "kind": "chat-history",
                  "options": {
                    "connection": {
                      "provider": "mongodb",
                      "mongoProvider": "external",
                      "connection-string": "mongodb://localhost:27017",
                      "database-name": "test-db",
                      "collection-name": "test-collection"
                    }
                  }
                }
              ]
            }
            """);

    /// <summary>
    /// A minimal single-threaded <see cref="SynchronizationContext"/> that processes posted
    /// callbacks sequentially on a dedicated background thread.  Used to reproduce the
    /// sync-over-async deadlock scenario described in issue #698.
    /// </summary>
    private sealed class SingleThreadedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue
            = new(new System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)>());

        public SingleThreadedSynchronizationContext()
        {
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(this);
                foreach (var (cb, state) in _queue.GetConsumingEnumerable())
                    cb(state);
            })
            { IsBackground = true, Name = "TestSingleThreadSC" };
            thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>
        /// Posts <paramref name="work"/> to the SC thread and returns a Task that completes
        /// when the returned Task from <paramref name="work"/> completes.
        /// </summary>
        public Task<T> PostAsync<T>(Func<Task<T>> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                work().ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) tcs.SetResult(t.Result);
                    else if (t.IsFaulted)
                        tcs.SetException(
                            t.Exception!.InnerExceptions.Count == 1
                                ? t.Exception.InnerException!
                                : t.Exception);
                    else tcs.SetCanceled();
                }, TaskScheduler.Default);
            }, null);
            return tcs.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    private static Task<AgentChat> CreateChatAsync(AgentDefinition agentDefinition, AgentServices? agentServices = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentServices = agentServices,
            });

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }
    }

    private sealed class RecordingAgentPersistenceStore : IAgentPersistenceStore
    {
        private int readCalls;
        private int storeCalls;
        private int restoreCalls;
        private readonly List<string> storedAgentSessionIds = [];
        private readonly TaskCompletionSource storeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PersistedAgent? RestoredAgent { get; init; }

        public int ReadCalls => this.readCalls;
        public int StoreCalls => this.storeCalls;
        public int RestoreCalls => this.restoreCalls;

        public IReadOnlyList<string> StoredAgentSessionIds => this.storedAgentSessionIds;

        public Task WaitForStoreCallAsync(CancellationToken cancellationToken = default)
            => this.storeSignal.Task.WaitAsync(cancellationToken);

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.storeCalls);
            this.storedAgentSessionIds.Add(request.Agent.AgentSessionId);
            this.storeSignal.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<PersistedAgent?> RestoreAsync(
            RestoreRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.restoreCalls);
            if (this.RestoredAgent is null)
            {
                return ValueTask.FromResult<PersistedAgent?>(null);
            }

            if (!string.Equals(this.RestoredAgent.Value.AgentSessionId, request.AgentSessionId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<PersistedAgent?>(null);
            }

            return ValueTask.FromResult(this.RestoredAgent);
        }

        public ValueTask<ChatMessage[]> ReadMessagesAsync(
            ReadMessagesRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.readCalls);
            return ValueTask.FromResult(Array.Empty<ChatMessage>());
        }

        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentSessionId>>(Array.Empty<AgentSessionId>());
    }

    private sealed class FixedApiKeyResolver(string key) : IApiKeyResolver
    {
        public string ResolveApiKey(string? apiKeyValue, string? serverName) => key;
        public Task<string> ResolveApiKeyAsync(string? apiKeyValue, string? serverName, CancellationToken cancellationToken = default) => Task.FromResult(key);
    }

    // ─── Fix #1187: hosted Copilot sub-agent AgentDefinition compatibility ─────

    [Fact]
    public void AgentFactory_CreateChatClient_HostedSubAgentDefinition_DoesNotThrow()
    {
        // Fix #1187: the full canonical hosted-Copilot sub-agent AgentDefinition (built by
        // CopilotSubAgentDefinitionDefaults.Create) must be accepted by AgentFactory and
        // resolve to the CopilotSubAgentChatClient — never trip the "Agent definition does
        // not specify a model." throw.
        var definition = Phantom.Workspaces.Llm.Interfaces.CopilotSubAgentDefinitionDefaults.Create(
            subAgentSessionId: "session-1187-factory-a",
            displayName: null,
            description: null,
            name: null);

        var result = AgentFactory.CreateChatClient(definition);

        Assert.IsType<CopilotSubAgentChatClient>(result.ChatClient);
    }

    [Fact]
    public void AgentFactory_CreateChatClient_HostedSubAgentDefinition_ResolvesProviderBeforeModelIdValidation()
    {
        // Fix #1187 (composes with #912): the github-copilot-subagent provider fast-path is
        // entered before the model-id validation. The canonical hosted sub-agent uses the
        // "cli-hosted" sentinel model.id — which is semantically unused because the CLI owns
        // model selection — and must still be accepted.
        var definition = Phantom.Workspaces.Llm.Interfaces.CopilotSubAgentDefinitionDefaults.Create(
            subAgentSessionId: "session-1187-factory-b",
            displayName: null,
            description: null,
            name: null);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        Assert.Equal(
            Phantom.Workspaces.Llm.Interfaces.CopilotSubAgentDefinitionDefaults.HostedSubAgentModelId,
            promptAgent.Model?.Id);

        var result = AgentFactory.CreateChatClient(definition);
        Assert.IsType<CopilotSubAgentChatClient>(result.ChatClient);
    }
}
