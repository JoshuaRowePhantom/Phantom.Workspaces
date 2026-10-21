using AgentSchema;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentFactorySteeringTests
{
    [Fact]
    public async Task CreateChatClient_GitHubModels_WithQueueManager_WrapsWithMiddleware()
    {
        var agent = LoadGitHubModelsAgent();
        var result = await AgentFactory.CreateChatClientAsync(
            agent,
            new AgentServices { AccessBroker = new AgentAccessTestBroker() },
            queueManager: new AgentInputQueueManager());

        Assert.IsType<ToolResultSteeringMiddleware>(result.ChatClient);
    }

    [Fact]
    public async Task CreateChatClient_GitHubModels_WithoutQueueManager_NoMiddleware()
    {
        var agent = LoadGitHubModelsAgent();
        var result = await AgentFactory.CreateChatClientAsync(
            agent,
            new AgentServices { AccessBroker = new AgentAccessTestBroker() },
            queueManager: null);

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

}
