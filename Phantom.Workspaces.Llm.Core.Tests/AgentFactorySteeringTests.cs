using AgentSchema;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactorySteeringTests
{
    [Fact]
    public void CreateChatClient_GitHubModels_WithQueueManager_WrapsWithMiddleware()
    {
        var original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var agent = LoadGitHubModelsAgent();
            var result = AgentFactory.CreateChatClient(agent, services: null, queueManager: new AgentInputQueueManager());

            Assert.IsType<ToolResultSteeringMiddleware>(result.ChatClient);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }

    [Fact]
    public void CreateChatClient_GitHubModels_WithoutQueueManager_NoMiddleware()
    {
        var original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "test-token");
        try
        {
            var agent = LoadGitHubModelsAgent();
            var result = AgentFactory.CreateChatClient(agent, services: null, queueManager: null);

            Assert.IsNotType<ToolResultSteeringMiddleware>(result.ChatClient);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
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
}
