using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Unit tests verifying that <see cref="AgentFactory.CreateChatClient"/> correctly wires
/// BYOK (bring-your-own-key) mode for the <c>github-copilot</c> provider.
/// </summary>
public sealed class AgentFactoryByokTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static AgentDefinition LoadByokAgent(
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
            "provider": "github-copilot",
            "connection": {
              "kind": "key",
              "endpoint": "{{endpoint}}",
              "apiKey": "{{apiKey}}"
            }{{optionsSection}}
          }
        }
        """);
    }

    private static AgentDefinition LoadStandardAgent(string? apiKey = null)
    {
        var connectionBody = apiKey is null
            ? @"""kind"": ""key"""
            : $@"""kind"": ""key"", ""apiKey"": ""{apiKey}""";

        return AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "standard-test",
          "model": {
            "id": "test-model",
            "provider": "github-copilot",
            "connection": { {{connectionBody}} }
          }
        }
        """);
    }

    // ---------------------------------------------------------------------------
    // BYOK mode — endpoint present
    // ---------------------------------------------------------------------------

    [Fact]
    public void CreateChatClient_GithubCopilot_WithEndpoint_ReturnsCopilotSdkChatClient()
    {
        var agent = LoadByokAgent("http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithEndpoint_ByokBaseUrl_IsEndpointFromConnection()
    {
        var agent = LoadByokAgent("http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("http://localhost:12345/", copilotClient.ByokOptions!.BaseUrl);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithEndpoint_ByokApiKey_IsResolved()
    {
        var agent = LoadByokAgent("http://localhost:12345/", apiKey: "my-api-key");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        Assert.Equal("my-api-key", copilotClient.ByokOptions!.ApiKey);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithEndpoint_GitHubToken_IsNull()
    {
        var agent = LoadByokAgent("http://localhost:12345/");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.GitHubToken);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithEndpoint_AllOptions_AreMapped()
    {
        // providerType, wireApi, wireModel, headers come from options.additionalProperties because
        // AgentSchema.ApiKeyConnection does not carry an AdditionalProperties bag; extra connection
        // fields are silently dropped by the AgentSchema parser.
        var agent = LoadByokAgent(
            "http://localhost:12345/",
            additionalOptions: @"""providerType"": ""azure"", ""wireApi"": ""chat-v2"", ""wireModel"": ""gpt-x"", ""headers"": {""X-Env"": ""prod""}");

        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(copilotClient.ByokOptions);
        var byok = copilotClient.ByokOptions!;
        Assert.Equal("azure", byok.ProviderType);
        Assert.Equal("chat-v2", byok.WireApi);
        Assert.Equal("gpt-x", byok.WireModel);
        Assert.NotNull(byok.Headers);
        Assert.Equal("prod", byok.Headers!["X-Env"]);
    }

    // ---------------------------------------------------------------------------
    // Standard mode — no endpoint
    // ---------------------------------------------------------------------------

    [Fact]
    public void CreateChatClient_GithubCopilot_WithoutEndpoint_NoByokOptions()
    {
        var agent = LoadStandardAgent(apiKey: "gh-pat");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.ByokOptions);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithoutEndpoint_ApiKey_BecomesGitHubToken()
    {
        var agent = LoadStandardAgent(apiKey: "gh-pat");
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Equal("gh-pat", copilotClient.GitHubToken);
    }

    [Fact]
    public void CreateChatClient_GithubCopilot_WithoutEndpoint_NoApiKey_GitHubTokenIsNull()
    {
        var agent = LoadStandardAgent();
        var result = AgentFactory.CreateChatClient(agent);

        var copilotClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.Null(copilotClient.GitHubToken);
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
    public void CreateChatClient_GithubCopilot_DisplayName_ByokMode()
    {
        var agent = LoadByokAgent("http://localhost:12345/");
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
    public async Task AgentFactory_GithubCopilot_ByokManifest_WiresBaseUrlFromConnection()
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
    }

    /// <summary>
    /// Opt-in end-to-end BYOK test that exercises the full Copilot CLI session against the
    /// local test server. Requires <c>COPILOT_BYOK_E2E=1</c>.
    /// </summary>
    [Fact]
    public async Task AgentFactory_GithubCopilot_ByokManifest_AgainstTestServer_EndToEnd()
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
