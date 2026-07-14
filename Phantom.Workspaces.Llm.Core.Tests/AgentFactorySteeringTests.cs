using AgentSchema;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactorySteeringTests
{
    [Fact]
    public void CreateChatClient_GitHubModels_WithQueueManager_WrapsWithMiddleware()
    {
        var agent = LoadGitHubModelsAgent();
        var result = AgentFactory.CreateChatClient(
            agent,
            services: null,
            queueManager: new AgentInputQueueManager(),
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        Assert.IsType<ToolResultSteeringMiddleware>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_GitHubModels_WithoutQueueManager_NoMiddleware()
    {
        var agent = LoadGitHubModelsAgent();
        var result = AgentFactory.CreateChatClient(
            agent,
            services: null,
            queueManager: null,
            apiKeyResolver: new FixedApiKeyResolver("test-token"));

        Assert.IsNotType<ToolResultSteeringMiddleware>(result.ChatClient);
    }

    [Fact]
    public void CreateChatClient_Echo_NoMiddlewareRegardlessOfQueueManager()
    {
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);

        var result = AgentFactory.CreateChatClient(agent, services: null, queueManager: new AgentInputQueueManager());

        Assert.IsType<EchoChatClient>(result.ChatClient);
    }

    private static AgentDefinition LoadGitHubModelsAgent()
        => AgentDefinitionLoader.LoadAgentFromJson(
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

    private sealed class FixedApiKeyResolver(string key) : IApiKeyResolver
    {
        public string ResolveApiKey(string? apiKeyValue, string? serverName) => key;
        public Task<string> ResolveApiKeyAsync(string? apiKeyValue, string? serverName, CancellationToken cancellationToken = default) => Task.FromResult(key);
    }
}
