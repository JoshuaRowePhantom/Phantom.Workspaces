using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Unit tests verifying that <see cref="AgentFactory.CreateChatClient"/> correctly wires
/// BYOK (bring-your-own-key) mode via the dedicated <c>openai</c> / <c>azure-openai</c>
/// provider strings, and built-in Copilot via <c>github-copilot</c> (issue #896). The factory
/// resolves only the connection (endpoint + API key); <c>model.options</c> is forwarded
/// verbatim to <see cref="CopilotSdkChatClient"/>, which interprets the BYOK wire knobs.
/// </summary>
public sealed class AgentFactoryByokTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AgentDefinition LoadByokAgent(
        string provider,
        string endpoint,
        string apiKey = "test-key",
        string? additionalOptions = null)
    {
        var optionsSection = additionalOptions is null
            ? string.Empty
            : $@",
            ""options"": {{
              ""additionalProperties"": {{ {additionalOptions} }}
            }}";

        return AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "byok-test",
          "model": {
            "id": "test-model",
            "provider": "{{provider}}",
            "connection": {
              "kind": "key",
              "endpoint": "{{endpoint}}",
              "apiKey": "{{apiKey}}"
            }{{optionsSection}}
          }
        }
        """);
    }

    private static AgentDefinition LoadStandardAgent(string? apiKey = null, string? additionalOptions = null)
    {
        var connectionBody = apiKey is null
            ? @"""kind"": ""key"""
            : $@"""kind"": ""key"", ""apiKey"": ""{apiKey}""";

        var optionsSection = additionalOptions is null
            ? string.Empty
            : $@",
            ""options"": {{
              ""additionalProperties"": {{ {additionalOptions} }}
            }}";

        return AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "standard-test",
          "model": {
            "id": "test-model",
            "provider": "github-copilot",
            "connection": { {{connectionBody}} }{{optionsSection}}
          }
        }
        """);
    }

    // ---------------------------------------------------------------------------
    // BYOK mode — openai / azure-openai provider strings
    // ---------------------------------------------------------------------------

    [Fact]
    public void CreateChatClient_OpenAi_ReturnsCopilotSdkChatClient()
    {
        var agent = LoadByokAgent("openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_AzureOpenAi_ReturnsCopilotSdkChatClient()
    {
        var agent = LoadByokAgent("azure-openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_OpenAi_ByokBaseUrl_IsEndpointFromConnection()
    {
        var agent = LoadByokAgent("openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("http://localhost:12345/", copilotClient.ByokOptions!.BaseUrl);
        Assert.Equal("openai", copilotClient.ByokOptions.Provider);
    }

    [Fact]
    public void CreateChatClient_AzureOpenAi_ByokProvider_IsAzureOpenAi()
    {
        var agent = LoadByokAgent("azure-openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("azure-openai", copilotClient.ByokOptions!.Provider);
    }

    [Fact]
    public void CreateChatClient_OpenAi_ApiKey_IsResolvedFromConnection()
    {
        var agent = LoadByokAgent("openai", "http://localhost:12345/", apiKey: "my-api-key");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("my-api-key", copilotClient.ByokOptions!.ApiKey);
    }

    [Fact]
    public void CreateChatClient_AzureOpenAi_ApiKey_IsResolvedFromConnection()
    {
        var agent = LoadByokAgent("azure-openai", "http://localhost:12345/", apiKey: "my-azure-key");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("my-azure-key", copilotClient.ByokOptions!.ApiKey);
    }

    [Fact]
    public void CreateChatClient_OpenAi_GitHubToken_IsNull()
    {
        var agent = LoadByokAgent("openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.GitHubToken);
    }

    [Fact]
    public void CreateChatClient_AzureOpenAi_GitHubToken_IsNull()
    {
        var agent = LoadByokAgent("azure-openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.GitHubToken);
    }

    [Fact]
    public void CreateChatClient_OpenAi_ModelOptions_ArePassedToClient()
    {
        // The factory forwards model.options verbatim; the client (not the factory) interprets
        // the BYOK wire knobs when building the SDK ProviderConfig.
        var agent = LoadByokAgent(
            "openai",
            "http://localhost:12345/",
            additionalOptions: @"""wireApi"": ""chat-v2"", ""wireModel"": ""gpt-x"", ""headers"": {""X-Env"": ""prod""}");

        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ModelOptions);
        Assert.Equal("chat-v2", copilotClient.ModelOptions!.AdditionalProperties?["wireApi"]);

        var providerConfig = CopilotSdkChatClient.CreateProviderConfig(
            copilotClient.ByokOptions!,
            "test-model",
            copilotClient.ModelOptions);
        Assert.Equal("openai", providerConfig.Type);
        Assert.Equal("chat-v2", providerConfig.WireApi);
        Assert.Equal("gpt-x", providerConfig.WireModel);
        Assert.NotNull(providerConfig.Headers);
        Assert.Equal("prod", providerConfig.Headers!["X-Env"]);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_ModelOptions_ArePassedToClient()
    {
        var agent = LoadStandardAgent(additionalOptions: @"""thinking"": ""medium""");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ModelOptions);
        Assert.Equal("medium", copilotClient.ModelOptions!.AdditionalProperties?["thinking"]);
    }

    [Fact]
    public void CreateChatClient_OpenAi_CliPath_FromModelOptions_IsMapped()
    {
        var agent = LoadByokAgent(
            "openai",
            "http://localhost:12345/",
            additionalOptions: @"""cliPath"": ""C:/tools/copilot.exe""");

        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal("C:/tools/copilot.exe", copilotClient.CliPath);
    }

    [Fact]
    public void CreateChatClient_OpenAi_NoCliPath_CliPathIsNull()
    {
        var agent = LoadByokAgent("openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.CliPath);
    }

    [Fact]
    public void CreateChatClient_OpenAi_WithoutEndpoint_Throws()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "byok-test",
          "model": {
            "id": "test-model",
            "provider": "openai",
            "connection": { "kind": "key", "apiKey": "test-key" }
          }
        }
        """);

        var exception = Assert.Throws<InvalidOperationException>(() => AgentFactory.CreateChatClient(agent));
        Assert.Contains("endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // Built-in mode — github-copilot provider string
    // ---------------------------------------------------------------------------

    [Fact]
    public void CreateChatClient_GithubCopilot_NoEndpoint_NoByokOptions()
    {
        var agent = LoadStandardAgent(apiKey: "gh-pat");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.ByokOptions);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_ApiKey_BecomesGitHubToken()
    {
        var agent = LoadStandardAgent(apiKey: "gh-pat");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal("gh-pat", copilotClient.GitHubToken);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_NoApiKey_GitHubTokenIsNull()
    {
        var agent = LoadStandardAgent();
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.GitHubToken);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithByokEndpoint_BuildsByokClientPointedAtEndpoint()
    {
        // Issue #1106: github-copilot + explicit endpoint routes through the Copilot SDK BYOK
        // client so schema and runtime agree. Replaces the old
        // CreateChatClient_GithubCopilot_WithEndpoint_Throws behaviour.
        var agent = LoadByokAgent("github-copilot", "http://localhost:12345/", apiKey: "my-key");

        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("http://localhost:12345/", copilotClient.ByokOptions!.BaseUrl);
        Assert.Equal("my-key", copilotClient.ByokOptions.ApiKey);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithByokEndpoint_ProviderDefaultsToOpenAi()
    {
        // Issue #1106: with no explicit connection.providerType, the BYOK wire provider defaults
        // to "openai" (matching the JSON schema's providerType default).
        var agent = LoadByokAgent("github-copilot", "http://localhost:12345/");

        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("openai", copilotClient.ByokOptions!.Provider);
    }

    [Fact]
    public void CreateChatClient_Workspaces256kManifest_ConstructsChatClientWithoutThrowing()
    {
        // Issue #1106: the exact "workspaces-256k" manifest shape (github-copilot + Ollama /v1
        // endpoint + apiType OpenAI) must deserialize and build a chat client without throwing.
        var agent = AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "workspaces-256k",
          "model": {
            "id": "qwen3.6",
            "provider": "github-copilot",
            "apiType": "OpenAI",
            "connection": {
              "kind": "key",
              "endpoint": "http://localhost:11434/v1",
              "apiKey": "ollama"
            },
            "options": {
              "temperature": 0.7,
              "topP": 0.9,
              "maxOutputTokens": 8192,
              "additionalProperties": {
                "num_ctx": 262144
              }
            }
          }
        }
        """);

        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("http://localhost:11434/v1", copilotClient.ByokOptions!.BaseUrl);
        Assert.Equal("ollama", copilotClient.ByokOptions.ApiKey);
        Assert.Equal("openai", copilotClient.ByokOptions.Provider);
    }

    [Fact]
    public async Task AgentFactory_GithubCopilot_ByokManifest_WiresBaseUrlFromConnection()
    {
        // Issue #1106: mirrors AgentFactory_OpenAi_ByokManifest_WiresBaseUrlFromConnection for the
        // github-copilot provider string.
        await using var server = new OpenAiCompatibleChatServer(new EchoChatClient());

        var manifestJson = $$"""
        {
            "name": "gh-copilot-byok-wiring-test",
            "displayName": "GH Copilot BYOK Wiring Test",
            "template": {
                "kind": "prompt",
                "name": "gh-copilot-byok-wiring-test",
                "model": {
                    "id": "test-model",
                    "provider": "github-copilot",
                    "connection": {
                        "kind": "key",
                        "endpoint": "{{server.BaseUrl}}",
                        "apiKey": "test-key"
                    }
                }
            }
        }
        """;

        var manifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, parameterValues: null);
        var result = AgentFactory.CreateChatClient(definition);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal(server.BaseUrl, copilotClient.ByokOptions!.BaseUrl);
        Assert.Equal("test-key", copilotClient.ByokOptions.ApiKey);
        Assert.Equal("openai", copilotClient.ByokOptions.Provider);
    }

    // ---------------------------------------------------------------------------
    // Display names
    // ---------------------------------------------------------------------------

    [Fact]
    public void CreateChatClient_GithubCopilot_DisplayName_StandardMode()
    {
        var agent = LoadStandardAgent();
        var result = AgentFactory.CreateChatClient(agent);

        Assert.Equal("GitHub Copilot (test-model)", result.DisplayName);
    }

    [Fact]
    public void CreateChatClient_OpenAi_DisplayName_ByokMode()
    {
        var agent = LoadByokAgent("openai", "http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        Assert.Equal("GitHub Copilot BYOK (test-model @ http://localhost:12345/)", result.DisplayName);
    }

    // ---------------------------------------------------------------------------
    // Integration tests — factory path via OpenAiCompatibleChatServer
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Deterministic wiring test: verifies that <see cref="AgentFactory.CreateChatClient"/>
    /// constructs a <see cref="CopilotSdkChatClient"/> whose <see cref="CopilotSdkChatClient.ByokOptions"/>
    /// are populated from the manifest connection, without requiring the Copilot CLI.
    /// </summary>
    [Fact]
    public async Task AgentFactory_OpenAi_ByokManifest_WiresBaseUrlFromConnection()
    {
        await using var server = new OpenAiCompatibleChatServer(new EchoChatClient());

        var manifestJson = $$"""
        {
            "name": "byok-wiring-test",
            "displayName": "BYOK Wiring Test",
            "template": {
                "kind": "prompt",
                "name": "byok-wiring-test",
                "model": {
                    "id": "test-model",
                    "provider": "openai",
                    "connection": {
                        "kind": "key",
                        "endpoint": "{{server.BaseUrl}}",
                        "apiKey": "test-key"
                    }
                }
            }
        }
        """;

        var manifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, parameterValues: null);
        var result = AgentFactory.CreateChatClient(definition);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal(server.BaseUrl, copilotClient.ByokOptions!.BaseUrl);
        Assert.Equal("test-key", copilotClient.ByokOptions.ApiKey);
        Assert.Equal("openai", copilotClient.ByokOptions.Provider);
    }

    /// <summary>
    /// Opt-in end-to-end BYOK test that exercises the full Copilot CLI session against the
    /// local test server. Requires <c>COPILOT_BYOK_E2E=1</c>.
    /// </summary>
    [Fact]
    public async Task AgentFactory_OpenAi_ByokManifest_AgainstTestServer_EndToEnd()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_BYOK_E2E") != "1")
        {
            return;
        }

        await using var server = new OpenAiCompatibleChatServer(new FixedResponseChatClient("factory-byok-pong"));

        var manifestJson = $$"""
        {
            "name": "byok-e2e-test",
            "displayName": "BYOK E2E Test",
            "template": {
                "kind": "prompt",
                "name": "byok-e2e-test",
                "model": {
                    "id": "test-model",
                    "provider": "openai",
                    "connection": {
                        "kind": "key",
                        "endpoint": "{{server.BaseUrl}}",
                        "apiKey": "test-key"
                    }
                }
            }
        }
        """;

        var manifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
        var definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, parameterValues: null);
        var result = AgentFactory.CreateChatClient(definition);

        var response = await result.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ping")]);

        Assert.Contains("factory-byok-pong", response.Text, StringComparison.Ordinal);
    }

    private sealed class FixedResponseChatClient(string content) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, content)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, content);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
