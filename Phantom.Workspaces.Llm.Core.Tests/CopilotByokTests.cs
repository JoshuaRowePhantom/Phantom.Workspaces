using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotByokTests
{
    [Fact]
    public void CreateProviderConfig_MapsByokOptions()
    {
        var byok = new CopilotByokOptions
        {
            BaseUrl = "http://localhost:1234/",
            ApiKey = "test-key",
            ProviderType = "openai",
            WireApi = "chat-completions",
            WireModel = "wire-model",
            Headers = new Dictionary<string, string> { ["X-Test"] = "1" },
        };

        var providerConfig = CopilotSdkChatClient.CreateProviderConfig(byok, "gpt-test");

        Assert.Equal("http://localhost:1234/", providerConfig.BaseUrl);
        Assert.Equal("test-key", providerConfig.ApiKey);
        Assert.Equal("openai", providerConfig.Type);
        Assert.Equal("chat-completions", providerConfig.WireApi);
        Assert.Equal("gpt-test", providerConfig.ModelId);
        Assert.Equal("wire-model", providerConfig.WireModel);
        Assert.NotNull(providerConfig.Headers);
        Assert.Equal("1", providerConfig.Headers!["X-Test"]);
    }

    [Fact]
    public async Task OpenAiCompatibleChatServer_ServesEchoProviderOverHttp()
    {
        await using var server = new OpenAiCompatibleChatServer(new EchoChatClient());
        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

        var request = new
        {
            model = "test",
            messages = new[]
            {
                new { role = "user", content = "byok-roundtrip" },
            },
        };

        using var response = await httpClient.PostAsJsonAsync("v1/chat/completions", request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        // The echo provider returns the last user message verbatim.
        Assert.Equal("byok-roundtrip", content);
    }

    /// <summary>
    /// End-to-end BYOK check that runs a real Copilot CLI session pointed at the local test
    /// server. It is opt-in (requires the Copilot CLI plus <c>COPILOT_BYOK_E2E=1</c>) so it never
    /// runs as part of the deterministic suite.
    /// </summary>
    [Fact]
    public async Task CopilotProvider_Byok_AgainstTestServer_EndToEnd()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_BYOK_E2E") != "1")
        {
            return;
        }

        await using var server = new OpenAiCompatibleChatServer(new FixedResponseChatClient("byok-pong"));
        var byok = new CopilotByokOptions
        {
            BaseUrl = server.BaseUrl,
            ApiKey = "test-key",
        };

        var cliPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        using var chatClient = new CopilotSdkChatClient(
            "gpt-test",
            "GitHub Copilot (BYOK test)",
            gitHubToken: null,
            loggerFactory: null,
            byokOptions: byok,
            cliPath: cliPath);

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);

        Assert.Contains("byok-pong", response.Text, StringComparison.Ordinal);
    }

    private sealed class FixedResponseChatClient : IChatClient
    {
        private readonly string content;

        public FixedResponseChatClient(string content) => this.content = content;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, this.content)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, this.content);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
