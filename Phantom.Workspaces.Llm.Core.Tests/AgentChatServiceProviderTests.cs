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

    private static AgentChat CreateChatWithServices(
        AgentServices? agentServices = null,
        IChatClient? clientOverride = null)
    {
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
        var persistenceStore = new InMemoryAgentPersistenceStore();
        return AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ConfiguredStore = persistenceStore,
            ClientOverride = clientOverride ?? new DeterministicTestChatClient(),
            DisplayNameOverride = "test-chat",
            AgentServices = agentServices,
        }).GetAwaiter().GetResult();
    }

    private sealed class StubRunningAgentChatFactory : RunningAgentChatFactoryKey { }

    private sealed class StubCopilotSubAgentReceiver : ICopilotSubAgentReceiver
    {
        public void Push(ChatResponseUpdate update) { }
        public void Complete() { }
        public void Fail(Exception exception) { }
    }

    /// <summary>
    /// A chat client that surfaces a specific service instance from <see cref="GetService"/>,
    /// so the primary delegation branch in <see cref="AgentChat.GetService"/>
    /// (<c>chatClientAgent?.GetService</c>) can be exercised end-to-end.
    /// </summary>
    private sealed class ServiceProvidingChatClient(Type serviceType, object? service) : IChatClient
    {
        private readonly DeterministicTestChatClient inner = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type type, object? serviceKey = null)
            => type == serviceType ? service : this.inner.GetService(type, serviceKey);

        public void Dispose() => this.inner.Dispose();
    }

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

    [Fact]
    public void AgentChat_GetService_ICopilotSubAgentReceiver_ReturnsCopilotSubAgentChatClient()
    {
        var receiver = new StubCopilotSubAgentReceiver();
        var clientOverride = new ServiceProvidingChatClient(typeof(ICopilotSubAgentReceiver), receiver);
        var agentChat = CreateChatWithServices(clientOverride: clientOverride);

        var result = agentChat.GetService(typeof(ICopilotSubAgentReceiver));

        Assert.Same(receiver, result);
    }

    [Fact]
    public void AgentChat_GetService_WhenChatClientReturnsNull_FallsBackToAgentServices()
    {
        var factory = new StubRunningAgentChatFactory();
        var agentServices = new AgentServices { RunningAgentChatFactory = factory };
        // The chat client explicitly returns null for the requested service type, so the primary
        // delegation branch (chatClientAgent?.GetService) yields null and the fallback to
        // AgentServices must supply the value.
        var clientOverride = new ServiceProvidingChatClient(typeof(RunningAgentChatFactoryKey), service: null);
        var agentChat = CreateChatWithServices(agentServices, clientOverride);

        var result = agentChat.GetService(typeof(RunningAgentChatFactoryKey));

        Assert.Same(factory, result);
    }
}
