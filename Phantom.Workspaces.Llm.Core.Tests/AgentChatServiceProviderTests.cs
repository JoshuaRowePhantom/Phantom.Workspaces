using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using RunningAgentChatFactoryKey = Phantom.Workspaces.Llm.Interfaces.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatServiceProviderTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentChat CreateChatWithServices(AgentServices? agentServices = null)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "test-chat",
            AgentServices = agentServices,
        }).GetAwaiter().GetResult();
    }

    private sealed class StubRunningAgentChatFactory : RunningAgentChatFactoryKey { }

    [Fact]
    public void AgentChat_GetService_IRunningAgentChatFactory_ReturnsFromAgentServices()
    {
        var factory = new StubRunningAgentChatFactory();
        var agentServices = new AgentServices { RunningAgentChatFactory = factory };
        var agentChat = CreateChatWithServices(agentServices);

        var result = agentChat.GetService(typeof(RunningAgentChatFactoryKey));

        Assert.Same(factory, result);
    }

    [Fact]
    public void AgentChat_GetService_ILoggerFactory_ReturnsFromAgentServices()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var agentServices = new AgentServices { LoggerFactory = loggerFactory };
        var agentChat = CreateChatWithServices(agentServices);

        var result = agentChat.GetService(typeof(ILoggerFactory));

        Assert.Same(loggerFactory, result);
    }

    [Fact]
    public void AgentChat_GetService_UnknownType_ReturnsNull()
    {
        var agentChat = CreateChatWithServices();

        var result = agentChat.GetService(typeof(string));

        Assert.Null(result);
    }

    [Fact]
    public void AgentChat_GetService_WhenNoChatClient_FallsBackToAgentServices()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var agentServices = new AgentServices { LoggerFactory = loggerFactory };
        var agentChat = CreateChatWithServices(agentServices);

        var result = agentChat.GetService(typeof(ILoggerFactory));

        Assert.Same(loggerFactory, result);
    }

    [Fact]
    public void AgentChat_GetService_WhenAgentServicesNull_ReturnsNull()
    {
        var agentChat = CreateChatWithServices(agentServices: null);

        var result = agentChat.GetService(typeof(ILoggerFactory));

        Assert.Null(result);
    }
}
