using AgentSchema;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using System.Security;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class AgentFactoryTests
{
    [Fact]
    public async Task AgentFactory_CreateAgentChat_SessionDefinitionWithSecret_MaterializesWithoutManifest()
    {
        // #1401: a session launch passes a prebuilt AgentDefinition and NO manifest. The inverted
        // gate must still materialize ${SECRET:...} — request the secret, rewrite to an opaque
        // handle, and populate the resolver.
        const string plaintext = "super-secret-token";
        var provider = new FakeSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString(plaintext);
        var definition = AgentDefinition.FromJson("""
        {
          "kind": "prompt",
          "name": "session-agent",
          "model": {
            "id": "gpt-test",
            "provider": "github-copilot",
            "connection": { "kind": "key", "apiKey": "${SECRET:GitHubToken}" }
          }
        }
        """) ?? throw new InvalidOperationException("Failed to load definition.");

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = definition,
            AgentSessionId = "session-1",
            AgentServices = new AgentServices { SecretProvider = provider, ChatClientOverride = new DeterministicTestChatClient() },
            PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
        });

        Assert.Equal(1, provider.CallCount);
        var promptAgent = Assert.IsType<PromptAgent>(chat.AgentDefinition);
        var connection = Assert.IsType<ApiKeyConnection>(promptAgent.Model!.Connection);
        Assert.StartsWith("${SECRET:", connection.ApiKey, StringComparison.Ordinal);
        Assert.NotEqual("${SECRET:GitHubToken}", connection.ApiKey);
        Assert.DoesNotContain(plaintext, chat.AgentDefinition!.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChatAsync_SecretInManifest_CallsSecretProviderAndKeepsDefinitionTokenized()
    {
        const string plaintext = "super-secret-token";
        var provider = new FakeSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString(plaintext);
        var manifest = LoadSecretManifest("${SECRET:GitHubToken}", provider: "github-copilot");

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentManifest = manifest,
            AgentServices = new AgentServices { SecretProvider = provider, ChatClientOverride = new DeterministicTestChatClient() },
            PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
        });

        Assert.Equal(1, provider.CallCount);
        var promptAgent = Assert.IsType<PromptAgent>(chat.AgentDefinition);
        var connection = Assert.IsType<ApiKeyConnection>(promptAgent.Model!.Connection);
        Assert.StartsWith("${SECRET:", connection.ApiKey, StringComparison.Ordinal);
        Assert.NotEqual("${SECRET:GitHubToken}", connection.ApiKey);
        Assert.DoesNotContain(plaintext, chat.AgentDefinition!.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChatAsync_SecretProviderRefuses_ThrowsSecretMaterializationRefusedException()
    {
        var provider = new FakeSecretProvider { ReturnNull = true };
        var manifest = LoadSecretManifest("${SECRET:GitHubToken}", provider: "github-copilot");

        var exception = await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() =>
            AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentManifest = manifest,
                AgentServices = new AgentServices { SecretProvider = provider },
                PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
            }));

        Assert.DoesNotContain("super-secret-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateChatClientAsync_SecretReferenceToken_ResolvesAtSdkSeamAndDisposesSecureString()
    {
        const string token = "${SECRET:handle}";
        const string plaintext = "byok-secret-token";
        var secure = ToSecureString(plaintext, makeReadOnly: false);
        var resolver = new SecretPlaceholderResolver();
        resolver.Register(token, new SecretRetriever
        {
            SecretName = "ApiKey",
            Secret = _ => Task.FromResult(secure),
        });
        var agent = AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "byok-agent",
          "model": {
            "id": "gpt-test",
            "provider": "openai",
            "connection": { "kind": "key", "endpoint": "http://localhost:12345/", "apiKey": "{{token}}" }
          }
        }
        """);

        var result = await AgentFactory.CreateChatClientAsync(
            agent,
            new AgentServices { SecretPlaceholderResolver = resolver });

        var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal(plaintext, client.ByokOptions!.ApiKey);
        Assert.Throws<ObjectDisposedException>(() => secure.AppendChar('x'));
    }

    [Fact]
    public async Task CreateChatClientAsync_McpToolSecretPlaceholder_TransportReceivesResolvedSecretNotRawPlaceholder()
    {
        // #1405: the shared gate materializes an MCP tool's ${SECRET:GitHubToken} into an opaque
        // handle registered with the resolver, so when McpTransportFactory builds the transport via
        // ResolveRequiredSecretOrEnvAsync (the exact seam in the reported stack trace) the resolved
        // secret — never the raw ${SECRET:GitHubToken} placeholder — reaches the transport.
        const string plaintext = "resolved-github-token";
        var provider = new FakeSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString(plaintext);
        var definition = AgentDefinition.FromJson("""
        {
          "kind": "prompt",
          "name": "mcp-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": [
            {
              "kind": "mcp",
              "name": "github-secret-gated",
              "serverName": "github-secret-gated",
              "connection": { "kind": "key", "endpoint": "http://127.0.0.1:1/", "apiKey": "${SECRET:GitHubToken}" },
              "approvalMode": { "kind": "never" }
            }
          ]
        }
        """) ?? throw new InvalidOperationException("Failed to load definition.");

        var (materializedDefinition, materializedServices) = await AgentFactory.MaterializeSecretsIfNeededAsync(
            definition,
            new AgentServices { SecretProvider = provider },
            manifest: null,
            agentSessionId: null,
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        var rewrittenApiKey = Regex.Match(materializedDefinition!.ToJson(), "\\$\\{SECRET:[^}]+\\}").Value;
        Assert.NotEqual("${SECRET:GitHubToken}", rewrittenApiKey);
        Assert.DoesNotContain("${SECRET:GitHubToken}", materializedDefinition.ToJson(), StringComparison.Ordinal);

        var resolved = await AgentFactory.ResolveRequiredSecretOrEnvAsync(
            rewrittenApiKey,
            materializedServices,
            serverName: "github-secret-gated");

        Assert.Equal(plaintext, resolved);
    }

    [Fact]
    public async Task CreateAgentChatAsync_NoSecretProvider_LeavesLegacySecretPlaceholderPathIntact()
    {
        var manifest = LoadSecretManifest("${SECRET:GitHubToken}", provider: "github-copilot", modelId: "test");

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentManifest = manifest,
            AgentServices = new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
        });

        var promptAgent = Assert.IsType<PromptAgent>(chat.AgentDefinition);
        var connection = Assert.IsType<ApiKeyConnection>(promptAgent.Model!.Connection);
        Assert.Equal("${SECRET:GitHubToken}", connection.ApiKey);
    }

    [Fact]
    public async Task AgentFactory_GitHubModelsConnection_SecretPlaceholder_ResolvesViaSecretResolver()
    {
        // #1398: a github-models connection whose apiKey is a ${SECRET:...} placeholder must resolve
        // through the secret resolver on the async factory path. If the resolver were bypassed the
        // placeholder would be treated as an environment-variable name and creation would throw.
        const string token = "${SECRET:GitHubModelsKey}";
        var resolver = new SecretPlaceholderResolver();
        resolver.Register(token, new SecretRetriever
        {
            SecretName = "GitHubModelsKey",
            Secret = _ => Task.FromResult(ToSecureString("resolved-models-key")),
        });
        var agent = AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "github-models-agent",
          "model": {
            "id": "gpt-4.1-mini",
            "provider": "github-models",
            "apiType": "OpenAI",
            "connection": { "kind": "key", "endpoint": "https://models.github.ai/inference", "apiKey": "{{token}}" }
          },
          "tools": []
        }
        """);

        var (client, displayName) = await AgentFactory.CreateChatClientAsync(
            agent,
            new AgentServices { SecretPlaceholderResolver = resolver });

        Assert.NotNull(client);
        Assert.Equal("GitHub Models (gpt-4.1-mini at https://models.github.ai/inference)", displayName);
    }

    [Fact]
    public async Task AgentFactory_GitHubCopilotByokConnection_SecretPlaceholder_ResolvesViaSecretResolver()
    {
        // #1398: a BYOK (openai/azure-openai) connection with a ${SECRET:...} apiKey resolves via the
        // secret resolver, so the resolved plaintext lands in the SDK's BYOK options.
        const string token = "${SECRET:ByokKey}";
        var resolver = new SecretPlaceholderResolver();
        resolver.Register(token, new SecretRetriever
        {
            SecretName = "ByokKey",
            Secret = _ => Task.FromResult(ToSecureString("resolved-byok-key")),
        });
        var agent = AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "byok-agent",
          "model": {
            "id": "gpt-test",
            "provider": "openai",
            "connection": { "kind": "key", "endpoint": "http://localhost:12345/", "apiKey": "{{token}}" }
          }
        }
        """);

        var result = await AgentFactory.CreateChatClientAsync(
            agent,
            new AgentServices { SecretPlaceholderResolver = resolver });

        var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal("resolved-byok-key", client.ByokOptions!.ApiKey);
    }

    [Fact]
    public void AgentFactory_ResolveApiKey_SyncMethodRemoved()
    {
        // #1398: the synchronous ResolveApiKey duplicate (and its IApiKeyResolver member) were
        // removed so that all API-key resolution flows through the async resolver-first path.
        var syncOnAgentFactory = typeof(AgentFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name == "ResolveApiKey")
            .ToArray();
        Assert.Empty(syncOnAgentFactory);

        var syncOnInterface = typeof(IApiKeyResolver).GetMethod("ResolveApiKey");
        Assert.Null(syncOnInterface);
    }

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

    [Fact]
    public void AgentFactory_AvailableToolsList_SetsAvailableTools()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "tools": ["read_agent", "list_agents"] }
            """);

        Assert.Equal(
            ["builtin:read_agent", "builtin:list_agents", "custom:*", "mcp:*"],
            client.BuiltinToolPolicyForTest!.AvailableTools);
        Assert.Null(client.BuiltinToolPolicyForTest.ExcludedTools);
    }

    [Fact]
    public void AgentFactory_AvailableToolsWildcard_LeavesAvailableToolsUnset()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "tools": ["*"] }
            """);

        Assert.Null(client.BuiltinToolPolicyForTest!.AvailableTools);
        Assert.Null(client.BuiltinToolPolicyForTest.ExcludedTools);
    }

    [Fact]
    public void AgentFactory_AvailableToolsMcpStar_MapsToMcpStarWithNoAutoAppend()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "tools": ["mcp:*"] }
            """);

        Assert.Equal(["mcp:*"], client.BuiltinToolPolicyForTest!.AvailableTools);
    }

    [Fact]
    public void AgentFactory_AvailableToolsMixedBareAndSourceQualified_PrefixesBareOnly()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "tools": ["read_agent", "mcp:*"] }
            """);

        Assert.Equal(["builtin:read_agent", "mcp:*"], client.BuiltinToolPolicyForTest!.AvailableTools);
    }

    [Fact]
    public void AgentFactory_AvailableToolsEmpty_SetsAvailableToolsToCustomAndMcpOnly()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "tools": [] }
            """);

        Assert.Equal(["custom:*", "mcp:*"], client.BuiltinToolPolicyForTest!.AvailableTools);
    }

    [Fact]
    public void AgentFactory_AvailableToolsIsolated_SetsAvailableToolsToIsolatedSet()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "isolated": true }
            """);

        var expected = new ToolSet()
            .AddBuiltIn(BuiltInTools.Isolated)
            .AddCustom("*")
            .AddMcp("*")
            .ToArray();
        Assert.Equal(expected, client.BuiltinToolPolicyForTest!.AvailableTools);
    }

    [Fact]
    public void AgentFactory_ExcludedToolsWildcard_SetsExcludedToolsToBuiltinStar()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "excluded-tools": { "tools": ["*"] }
            """);

        Assert.Null(client.BuiltinToolPolicyForTest!.AvailableTools);
        Assert.Equal(["builtin:*"], client.BuiltinToolPolicyForTest.ExcludedTools);
    }

    [Fact]
    public void AgentFactory_ExcludedToolsBuiltinStar_MapsToBuiltinStarVerbatim()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "excluded-tools": { "tools": ["builtin:*"] }
            """);

        Assert.Equal(["builtin:*"], client.BuiltinToolPolicyForTest!.ExcludedTools);
    }

    [Fact]
    public void AgentFactory_ExcludedToolsIsolated_SetsExcludedToolsToIsolatedSet()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "excluded-tools": { "isolated": true }
            """);

        var expected = new ToolSet().AddBuiltIn(BuiltInTools.Isolated).ToArray();
        Assert.Equal(expected, client.BuiltinToolPolicyForTest!.ExcludedTools);
    }

    [Fact]
    public void AgentFactory_AvailableAndExcludedCombined_AppliesBoth()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "isolated": true },
            "excluded-tools": { "tools": ["exit_plan_mode"] }
            """);

        Assert.Equal("builtin:ask_user", client.BuiltinToolPolicyForTest!.AvailableTools![0]);
        Assert.Contains("custom:*", client.BuiltinToolPolicyForTest.AvailableTools);
        Assert.Contains("mcp:*", client.BuiltinToolPolicyForTest.AvailableTools);
        Assert.Equal(["builtin:exit_plan_mode"], client.BuiltinToolPolicyForTest.ExcludedTools);
    }

    [Fact]
    public void AgentFactory_NoBuiltinToolsEntry_LeavesDefaults()
    {
        var agent = LoadCopilotAgent("[]");

        var result = AgentFactory.CreateChatClient(
            agent,
            services: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(client.BuiltinToolPolicyForTest);
    }

    [Fact]
    public void AgentFactory_BuiltinToolsEntry_IsNotForwardedToToolsetFactory()
    {
        var agent = LoadCopilotAgent(
            """
            [
              {
                "kind": "github-cli-builtin-tools",
                "available-tools": { "tools": ["read_agent"] }
              },
              {
                "kind": "web_request",
                "name": "web_request"
              }
            ]
            """);

        var tools = AgentFactory.ExtractTools(agent);

        Assert.Collection(
            tools!,
            tool => Assert.Equal("web_request", Assert.IsType<CustomTool>(tool).Kind));
    }

    [Fact]
    public void AgentFactory_ClientModeEmpty_WithAvailableTools_ConstructsClientWithEmptyMode()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "client-mode": "empty",
            "available-tools": { "tools": ["mcp:*"] }
            """);

        Assert.Equal(CopilotClientMode.Empty, client.BuiltinToolPolicyForTest!.ClientMode);
    }

    [Fact]
    public void AgentFactory_ClientModeCopilotCli_IsDefault()
    {
        var client = CreateCopilotClientWithBuiltinTools(
            """
            "available-tools": { "tools": ["mcp:*"] }
            """);

        Assert.Equal(CopilotClientMode.CopilotCli, client.BuiltinToolPolicyForTest!.ClientMode);
    }

    [Fact]
    public void AgentFactory_ClientModeEmpty_WithoutAvailableTools_Throws()
    {
        var agent = new PromptAgent
        {
            Name = "github-copilot-agent",
            Model = new Model
            {
                Id = "gpt-5",
                Provider = "github-copilot",
                ApiType = "OpenAI",
                Connection = new ApiKeyConnection { ApiKey = "${GITHUB_TOKEN}" },
            },
            Tools =
            [
                new GitHubCliBuiltinToolsTool
                {
                    Kind = GitHubCliBuiltinToolsTool.KindName,
                    ClientMode = CopilotClientMode.Empty,
                },
            ],
        };

        var exception = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateChatClient(
            agent,
            services: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token")));
        Assert.Contains("client-mode: empty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("available-tools", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentDefinitionLoader_GithubCliBuiltinToolsEntry_IsExtractedAsTypedSubclass()
    {
        var agent = LoadCopilotAgent(
            """
            [
              {
                "kind": "github-cli-builtin-tools",
                "client-mode": "empty",
                "available-tools": { "tools": ["mcp:*"] },
                "excluded-tools": { "tools": ["shell"] }
              }
            ]
            """);

        var promptAgent = Assert.IsType<PromptAgent>(agent);
        var tool = Assert.IsType<GitHubCliBuiltinToolsTool>(Assert.Single(promptAgent.Tools!));
        Assert.Equal(CopilotClientMode.Empty, tool.ClientMode);
        Assert.Equal(["mcp:*"], tool.AvailableTools!.Tools);
        Assert.Equal(["shell"], tool.ExcludedTools!.Tools);
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
    public void CreateAgentChatAsync_CopilotPath_UsesHostSuppliedCurrentSessionContext()
    {
        var user = MakeSnapshot(["entity", "user"], ["users", "username", "alice"]);
        var profile = MakeSnapshot(["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var computer = MakeSnapshot(["entity", "computer"], ["computers", "hostname", "host-a"]);
        var hostContext = new CurrentSessionContext
        {
            AgentSessionId = "will-be-overwritten",
            User = user,
            UserComputerProfile = profile,
            Computer = computer,
        };
        var services = new AgentServices { CurrentSessionContext = hostContext };

        var resolved = AgentFactory.ResolveSessionContext(services, "session-42");

        Assert.Equal("session-42", resolved.AgentSessionId);
        Assert.Same(user, resolved.User);
        Assert.Same(profile, resolved.UserComputerProfile);
        Assert.Same(computer, resolved.Computer);
    }

    [Fact]
    public void CreateAgentChatAsync_CopilotPath_NoHostContext_FallsBackToMinimalContext()
    {
        var services = new AgentServices();

        var resolved = AgentFactory.ResolveSessionContext(services, "session-42");

        Assert.Equal("session-42", resolved.AgentSessionId);
        Assert.Null(resolved.User);
        Assert.Null(resolved.UserComputerProfile);
        Assert.Null(resolved.Computer);
    }

    private static Data.EntitySnapshot MakeSnapshot(string[] entityTypes, string[] entityName)
    {
        var entityId = new Data.EntityId();
        using var document = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": {{System.Text.Json.JsonSerializer.Serialize(entityTypes)}},
              "names": [{{System.Text.Json.JsonSerializer.Serialize(entityName)}}]
            }
            """);
        return new Data.EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = new Data.ConcurrencyTag("1"),
            ModifiedTime = new Data.Timestamp(System.DateTimeOffset.UtcNow, System.Guid.NewGuid().ToString()),
            Data = document.RootElement.Clone(),
            Relationships = System.Array.Empty<Data.EntitySnapshot>(),
        };
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

        // Inject a fake factory so the chat-history tool drives store creation without ever
        // touching Docker/MongoDB. The factory captures the definition for assertion.
        ChatHistoryProviderDefinition? observedDefinition = null;
        var invocationCount = 0;
        ValueTask<IAgentPersistenceStore> FakeFactory(
            ChatHistoryProviderDefinition? definition, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocationCount);
            observedDefinition = definition;
            return ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore());
        }

        await using var chat = await CreateChatAsync(agent, persistenceStoreFactory: FakeFactory);

        Assert.NotNull(chat);
        Assert.Equal(1, invocationCount);
        var mongo = Assert.IsType<MongoDbChatHistoryProviderDefinition>(observedDefinition);
        Assert.Equal("container", mongo.MongoProvider);
        Assert.Equal("test-db", mongo.DatabaseName);
        Assert.Equal("test-collection", mongo.CollectionName);
        Assert.Equal("test-mongo", mongo.ContainerName);
        Assert.Equal("/tmp/mongo", mongo.DataDirectory);
        chat.EnqueueUserMessage("hello");
    }

    [Fact]
    public async Task CreateAgentChat_NoChatHistoryTool_InvokesPersistenceStoreFactoryWithNullDefinition()
    {
        var agentJson = """
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
            """;

        var agent = AgentDefinitionLoader.LoadAgentFromJson(agentJson);

        ChatHistoryProviderDefinition? observedDefinition = null;
        var invocationCount = 0;
        var store = new InMemoryAgentPersistenceStore();
        ValueTask<IAgentPersistenceStore> FakeFactory(
            ChatHistoryProviderDefinition? definition, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocationCount);
            observedDefinition = definition;
            return ValueTask.FromResult<IAgentPersistenceStore>(store);
        }

        await using var chat = await CreateChatAsync(agent, persistenceStoreFactory: FakeFactory);

        Assert.NotNull(chat);
        Assert.Equal(1, invocationCount);
        Assert.Null(observedDefinition);
        Assert.Same(store, GetConfiguredStore(chat));
    }

    [Fact]
    public async Task CreateAgentChat_DefaultFactory_NoChatHistoryTool_UsesInMemoryStore()
    {
        var agent = CreateEchoPromptAgentDefinition();

        // No injected factory: the default delegate must map a null definition to an in-memory store.
        await using var chat = await CreateChatAsync(agent);

        Assert.NotNull(chat);
        Assert.IsType<InMemoryAgentPersistenceStore>(GetConfiguredStore(chat));
    }

    [Fact]
    public async Task CreateAgentChat_AgentPersistenceStoreOverride_TakesPrecedenceOverPersistenceStoreFactory()
    {
        var agent = LoadEchoAgentWithChatHistoryTool();
        var overrideStore = new RecordingAgentPersistenceStore();
        var services = new AgentServices { AgentPersistenceStoreOverride = overrideStore };

        var factoryInvoked = false;
        ValueTask<IAgentPersistenceStore> FakeFactory(
            ChatHistoryProviderDefinition? definition, CancellationToken cancellationToken)
        {
            factoryInvoked = true;
            return ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore());
        }

        await using var chat = await CreateChatAsync(agent, services, persistenceStoreFactory: FakeFactory);

        Assert.NotNull(chat);
        Assert.False(factoryInvoked, "AgentPersistenceStoreOverride should short-circuit the factory.");
        Assert.Same(overrideStore, GetConfiguredStore(chat));
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

    private static Task<AgentChat> CreateChatAsync(
        AgentDefinition agentDefinition,
        AgentServices? agentServices = null,
        Func<ChatHistoryProviderDefinition?, CancellationToken, ValueTask<IAgentPersistenceStore>>? persistenceStoreFactory = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                AgentServices = agentServices,
                PersistenceStoreFactory = persistenceStoreFactory,
            });

    // Reads the persistence store the AgentChat was configured with. AgentChat holds it on the
    // internal request record; InternalsVisibleTo makes the type visible to the test project.
    private static IAgentPersistenceStore GetConfiguredStore(AgentChat chat)
    {
        var field = typeof(AgentChat).GetField("request", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AgentChat.request field not found.");
        var request = (InternalCreateAgentChatRequest)field.GetValue(chat)!;
        return request.ConfiguredStore;
    }

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

    private static CopilotSdkChatClient CreateCopilotClientWithBuiltinTools(string builtinToolProperties)
    {
        var agent = LoadCopilotAgent(
            $$"""
            [
              {
                "kind": "github-cli-builtin-tools",
                {{builtinToolProperties}}
              }
            ]
            """);

        var result = AgentFactory.CreateChatClient(
            agent,
            services: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        return Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
    }

    private static AgentDefinition LoadCopilotAgent(string toolsJson)
        => AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "github-copilot-agent",
              "model": {
                "id": "gpt-5",
                "provider": "github-copilot",
                "apiType": "OpenAI",
                "connection": {
                  "kind": "key",
                  "apiKey": "${GITHUB_TOKEN}"
                }
              },
              "tools": {{toolsJson}}
            }
            """);

    private sealed class FixedApiKeyResolver(string key) : IApiKeyResolver
    {
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

    private static AgentManifest LoadSecretManifest(string apiKey, string provider, string modelId = "gpt-test")
        => AgentManifestLoader.LoadManifestFromJson($$"""
        {
          "name": "secret-agent",
          "displayName": "Secret Agent",
          "metadata": { "entity-id": "11111111-1111-1111-1111-111111111111" },
          "template": {
            "kind": "prompt",
            "name": "secret-agent",
            "model": {
              "id": "{{modelId}}",
              "provider": "{{provider}}",
              "connection": { "kind": "key", "apiKey": "{{apiKey}}" }
            }
          }
        }
        """);

    private static SecureString ToSecureString(string value, bool makeReadOnly = true)
    {
        var secure = new SecureString();
        foreach (var ch in value)
        {
            secure.AppendChar(ch);
        }

        if (makeReadOnly)
        {
            secure.MakeReadOnly();
        }

        return secure;
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public int CallCount { get; private set; }
        public bool ReturnNull { get; set; }
        public Dictionary<string, SecureString> Secrets { get; } = [];

        public Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)
        {
            this.CallCount++;
            if (this.ReturnNull)
            {
                return Task.FromResult<RequestSecretsResult?>(null);
            }

            var retrievers = requests
                .Where(request => this.Secrets.ContainsKey(request.SecretName))
                .Select(request => new SecretRetriever
                {
                    SecretName = request.SecretName,
                    Secret = _ => Task.FromResult(this.Secrets[request.SecretName]),
                })
                .ToArray();

            return Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult(retrievers, []));
        }
    }}
