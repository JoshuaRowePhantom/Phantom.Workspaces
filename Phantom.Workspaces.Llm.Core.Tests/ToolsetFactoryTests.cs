using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ToolsetFactoryTests
{
    [Fact]
    public async Task CreateWebSearchToolsetFactory_ReturnsWebSearchTool()
    {
        var factory = ToolsetFactory.CreateDefaultToolsetFactory();

        var toolset = await factory.CreateToolsetAsync("web_search", [], new AgentServices());
        var tools = await toolset.ListToolsAsync();

        var tool = Assert.Single(tools);
        Assert.Equal("web_search", tool.Name);
    }

    [Fact]
    public async Task CreateWebToolsetFactory_ReturnsWebSearchAndWebRequestTools()
    {
        var factory = ToolsetFactory.CreateDefaultToolsetFactory();

        var toolset = await factory.CreateToolsetAsync("web", [], new AgentServices());
        var names = (await toolset.ListToolsAsync())
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["web_request", "web_search"], names);
    }

    [Fact]
    public async Task CreateNamedToolsetFactory_InvokesDelegateWithUnderlyingInstanceAndProperties()
    {
        var underlyingInstance = StubToolsetFactory.Instance;
        var expectedProperties = new Dictionary<string, object>
        {
            ["flag"] = true,
        };
        var called = false;
        var services = new AgentServices();
        var factory = ToolsetFactory.CreateNamedToolsetFactory(
            name: "custom",
            createToolsetAsync: (name, properties, agentServices) =>
            {
                called = true;
                Assert.Equal("custom", name);
                Assert.True((bool)properties["flag"]);
                Assert.Same(services, agentServices);
                IToolset toolset = new TestToolset();
                return Task.FromResult(toolset);
            },
            underlyingInstance: underlyingInstance);

        _ = await factory.CreateToolsetAsync("custom", expectedProperties, services);

        Assert.True(called);
    }

    [Fact]
    public async Task CreateDefaultToolsetFactory_CreatesFilesystemToolsetByName()
    {
        var factory = ToolsetFactory.CreateDefaultToolsetFactory();

        var toolset = await factory.CreateToolsetAsync("filesystem", [], new AgentServices());

        Assert.IsType<FilesystemServiceToolset>(toolset);
    }

    private sealed class TestToolset : IToolset
    {
        public Task<IList<Microsoft.Extensions.AI.AITool>> ListToolsAsync()
        {
            return Task.FromResult<IList<Microsoft.Extensions.AI.AITool>>([]);
        }
    }

    private sealed class StubToolsetFactory : IToolsetFactory
    {
        public static readonly StubToolsetFactory Instance = new();

        public Task<IToolset> CreateToolsetAsync(
            string name,
            Dictionary<string, object> properties,
            AgentServices agentServices)
        {
            _ = name;
            _ = properties;
            _ = agentServices;
            throw new InvalidOperationException("Not used by this test.");
        }
    }
}
