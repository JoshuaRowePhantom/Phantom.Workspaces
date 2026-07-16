using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using RunningAgentChatFactoryKey = Phantom.Workspaces.Llm.Interfaces.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentServicesTests
{
    private sealed class StubRunningAgentChatFactory : RunningAgentChatFactoryKey { }

    private sealed class StubToolsetFactory : IToolsetFactory
    {
        public Task<Microsoft.Agents.AI.AIContextProvider?> CreateToolsetAsync(AgentSchema.Tool tool, AgentServices agentServices)
            => Task.FromResult<Microsoft.Agents.AI.AIContextProvider?>(null);
    }

    private sealed class StubToolResourceFactory : IToolResourceFactory
    {
        public Task<AgentSchema.Tool?> ResolveToolResourceAsync(AgentSchema.ToolResource toolResource, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSchema.Tool?>(null);
    }

    private sealed class StubAccountUpsertService : IGitHubAccountUpsertService
    {
        public Task UpsertForTokenAsync(string token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void AgentServices_GetService_ReturnsNullForUnknownType()
    {
        var services = new AgentServices();

        var result = services.GetService(typeof(string));

        Assert.Null(result);
    }

    [Fact]
    public void AgentServices_GetService_ReturnsRegisteredService_ForEachKnownType()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var persistenceStore = new InMemoryAgentPersistenceStore();
        var runningAgentChatFactory = new StubRunningAgentChatFactory();
        var toolsetFactory = new StubToolsetFactory();
        var toolResourceFactory = new StubToolResourceFactory();

        var services = new AgentServices
        {
            LoggerFactory = loggerFactory,
            AgentPersistenceStoreOverride = persistenceStore,
            RunningAgentChatFactory = runningAgentChatFactory,
            ToolsetFactory = toolsetFactory,
            ToolResourceFactory = toolResourceFactory,
        };

        Assert.Same(loggerFactory, services.GetService(typeof(ILoggerFactory)));
        Assert.Same(persistenceStore, services.GetService(typeof(IAgentPersistenceStore)));
        Assert.Same(runningAgentChatFactory, services.GetService(typeof(RunningAgentChatFactoryKey)));
        Assert.Same(toolsetFactory, services.GetService(typeof(IToolsetFactory)));
        Assert.Same(toolResourceFactory, services.GetService(typeof(IToolResourceFactory)));
    }

    [Fact]
    public void AgentServices_GetService_ReturnsRegisteredAccountUpsertService()
    {
        var upsertService = new StubAccountUpsertService();

        var services = new AgentServices
        {
            AccountUpsertService = upsertService,
        };

        Assert.Same(upsertService, services.AccountUpsertService);
        Assert.Same(upsertService, services.GetService(typeof(IGitHubAccountUpsertService)));
    }
}
