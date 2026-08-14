using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ToolsetFactoryTests
{
    [Fact]
    public async Task CreateFixedToolset_ReturnsProvidedTools()
    {
        var fixedToolset = ToolsetFactory.CreateFixedToolset(
            new WebSearchTool(),
            new WebRequestTool());

        var tools = await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(fixedToolset));

        Assert.Equal(2, tools.Length);
        Assert.Equal("web_search", tools[0].Name);
        Assert.Equal("web_request", tools[1].Name);
    }

    [Fact]
    public async Task CreateNamedToolsetFactory_WhenKindMatches_UsesDelegate()
    {
        var called = false;
        var factory = ToolsetFactory.CreateNamedToolsetFactory(
            kind: "custom_kind",
            createToolsetAsync: (tool, _) =>
            {
                called = true;
                Assert.Equal("custom_kind", tool.Kind);
                return Task.FromResult<AIContextProvider?>(ToolsetFactory.CreateFixedToolset(new WebSearchTool()));
            },
            underlyingInstance: CreateMockToolsetFactory((_, _) => Task.FromResult<AIContextProvider?>(null)).Object);

        var toolset = await factory.CreateToolsetAsync(
            CreateCustomTool("custom_kind"),
            new AgentServices());

        Assert.True(called);
        Assert.NotNull(toolset);
        var tools = await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(toolset));
        Assert.Single(tools);
        Assert.Equal("web_search", tools[0].Name);
    }

    [Fact]
    public async Task CreateNamedToolsetFactory_WhenKindDiffers_UsesUnderlyingFactory()
    {
        var underlyingCalled = false;
        var underlying = CreateMockToolsetFactory((tool, _) =>
        {
            underlyingCalled = true;
            Assert.Equal("other_kind", tool.Kind);
            return Task.FromResult<AIContextProvider?>(ToolsetFactory.CreateFixedToolset(new WebRequestTool()));
        });

        var factory = ToolsetFactory.CreateNamedToolsetFactory(
            kind: "custom_kind",
            createToolsetAsync: (_, _) => Task.FromResult<AIContextProvider?>(ToolsetFactory.CreateFixedToolset(new WebSearchTool())),
            underlyingInstance: underlying.Object);

        var toolset = await factory.CreateToolsetAsync(
            CreateCustomTool("other_kind"),
            new AgentServices());

        Assert.True(underlyingCalled);
        Assert.NotNull(toolset);
        var tools = await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(toolset));
        Assert.Single(tools);
        Assert.Equal("web_request", tools[0].Name);
    }

    [Fact]
    public async Task CreateWebSearchToolsetFactory_ReturnsWebSearchToolset()
    {
        var factory = ToolsetFactory.CreateWebSearchToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("web_search"), new AgentServices());

        Assert.NotNull(toolset);
        var tools = await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(toolset));
        Assert.Single(tools);
        Assert.Equal("web_search", tools[0].Name);
    }

    [Fact]
    public async Task CreateWebRequestToolsetFactory_ReturnsWebRequestToolset()
    {
        var factory = ToolsetFactory.CreateWebRequestToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("web_request"), new AgentServices());

        Assert.NotNull(toolset);
        var tools = await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(toolset));
        Assert.Single(tools);
        Assert.Equal("web_request", tools[0].Name);
    }

    [Fact]
    public async Task CreateWebToolsetFactory_ReturnsWebSearchAndWebRequestToolset()
    {
        var factory = ToolsetFactory.CreateWebToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("web"), new AgentServices());

        Assert.NotNull(toolset);
        var names = (await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(toolset)))
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["web_request", "web_search"], names);
    }

    [Fact]
    public async Task CreateFilesystemToolsetFactory_ReturnsFilesystemServiceToolset()
    {
        var factory = ToolsetFactory.CreateFilesystemToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("filesystem"), new AgentServices());

        Assert.IsType<FilesystemServiceContextProvider>(toolset);
    }

    [Fact]
    public async Task CreateDefaultToolsetFactory_ReturnsNullForUnknownKind()
    {
        var factory = ToolsetFactory.CreateDefaultToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("unknown_kind"), new AgentServices());

        Assert.Null(toolset);
    }

    [Fact]
    public async Task CreateAgentSessionToolsetFactory_WhenKindMatches_ReturnsAgentSessionToolset()
    {
        var runningFactory = new StubRunningAgentChatFactory();
        var sessionContext = new CurrentSessionContext { AgentSessionId = "test-session" };
        var chatRef = new AgentChatRef();
        var agentServices = new AgentServices
        {
            RunningAgentChatFactory = runningFactory,
            CurrentSessionContext = sessionContext,
            CurrentAgentChatRef = chatRef,
        };
        var factory = ToolsetFactory.CreateAgentSessionToolsetFactory();

        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("agent-session"), agentServices);

        Assert.IsType<AgentSessionToolset>(toolset);
    }

    [Fact]
    public async Task CreateAgentSessionToolsetFactory_WhenKindDiffers_DefersToUnderlying()
    {
        var underlyingCalled = false;
        var underlying = CreateMockToolsetFactory((tool, _) =>
        {
            underlyingCalled = true;
            Assert.Equal("other_kind", tool.Kind);
            return Task.FromResult<AIContextProvider?>(ToolsetFactory.CreateFixedToolset(new WebRequestTool()));
        });
        var factory = ToolsetFactory.CreateAgentSessionToolsetFactory(underlying.Object);

        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("other_kind"), new AgentServices());

        Assert.True(underlyingCalled);
        Assert.NotNull(toolset);
    }

    [Fact]
    public async Task CreateAgentSessionToolsetFactory_WhenRunningAgentChatFactoryMissing_Throws()
    {
        var factory = ToolsetFactory.CreateAgentSessionToolsetFactory();
        var agentServices = new AgentServices
        {
            CurrentSessionContext = new CurrentSessionContext { AgentSessionId = "test-session" },
            CurrentAgentChatRef = new AgentChatRef(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateToolsetAsync(CreateCustomTool("agent-session"), agentServices));
        Assert.Contains("RunningAgentChatFactory", ex.Message);
    }

    [Fact]
    public async Task CreateAgentSessionToolsetFactory_WhenCurrentSessionContextMissing_Throws()
    {
        var factory = ToolsetFactory.CreateAgentSessionToolsetFactory();
        var agentServices = new AgentServices
        {
            RunningAgentChatFactory = new StubRunningAgentChatFactory(),
            CurrentAgentChatRef = new AgentChatRef(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateToolsetAsync(CreateCustomTool("agent-session"), agentServices));
        Assert.Contains("CurrentSessionContext", ex.Message);
    }

    [Fact]
    public async Task CreateAgentSessionToolsetFactory_WhenCurrentAgentChatRefMissing_Throws()
    {
        var factory = ToolsetFactory.CreateAgentSessionToolsetFactory();
        var agentServices = new AgentServices
        {
            RunningAgentChatFactory = new StubRunningAgentChatFactory(),
            CurrentSessionContext = new CurrentSessionContext { AgentSessionId = "test-session" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateToolsetAsync(CreateCustomTool("agent-session"), agentServices));
        Assert.Contains("CurrentAgentChatRef", ex.Message);
    }

    private sealed class StubRunningAgentChatFactory : IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, string? nameOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Combine_UsesFirstMatchingFactory()
    {
        var first = CreateMockToolsetFactory((tool, _) =>
            Task.FromResult<AIContextProvider?>(
                tool.Kind == "one" ? ToolsetFactory.CreateFixedToolset(new WebSearchTool()) : null));
        var second = CreateMockToolsetFactory((tool, _) =>
            Task.FromResult<AIContextProvider?>(
                tool.Kind == "two" ? ToolsetFactory.CreateFixedToolset(new WebRequestTool()) : null));
        var combined = ToolsetFactory.Combine(first.Object, second.Object);

        var oneToolset = await combined.CreateToolsetAsync(CreateCustomTool("one"), new AgentServices());
        var twoToolset = await combined.CreateToolsetAsync(CreateCustomTool("two"), new AgentServices());
        var missingToolset = await combined.CreateToolsetAsync(CreateCustomTool("three"), new AgentServices());

        Assert.NotNull(oneToolset);
        Assert.NotNull(twoToolset);
        Assert.Null(missingToolset);
        Assert.Equal("web_search", (await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(oneToolset!)))[0].Name);
        Assert.Equal("web_request", (await GetToolsAsync(Assert.IsType<FixedToolsContextProvider>(twoToolset!)))[0].Name);
    }

    private static Tool CreateCustomTool(string kind, object? connection = null)
    {
        var connectionJson = connection is null ? "null" : JsonSerializer.Serialize(connection);
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "tool-test-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                {
                  "kind": "{{kind}}",
                  "description": "test tool",
                  "connection": {{connectionJson}}
                }
              ]
            }
            """);

        var promptAgent = Assert.IsType<PromptAgent>(definition);
        return Assert.IsType<CustomTool>(Assert.Single(promptAgent.Tools!));
    }

    private static Mock<IToolsetFactory> CreateMockToolsetFactory(
        Func<Tool, AgentServices, Task<AIContextProvider?>> createToolsetAsync)
    {
        var mock = new Mock<IToolsetFactory>();
        mock.Setup(factory => factory.CreateToolsetAsync(It.IsAny<Tool>(), It.IsAny<AgentServices>()))
            .Returns((Tool tool, AgentServices services) => createToolsetAsync(tool, services));
        return mock;
    }

    private static async Task<AITool[]> GetToolsAsync(AIContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }
}
