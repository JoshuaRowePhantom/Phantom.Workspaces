using AgentSchema;
using Microsoft.Extensions.AI;
using Moq;
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

        var tools = await fixedToolset.ListToolsAsync();

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
                return Task.FromResult<IToolset?>(ToolsetFactory.CreateFixedToolset(new WebSearchTool()));
            },
            underlyingInstance: CreateMockToolsetFactory((_, _) => Task.FromResult<IToolset?>(null)).Object);

        var toolset = await factory.CreateToolsetAsync(
            CreateCustomTool("custom_kind"),
            new AgentServices());

        Assert.True(called);
        Assert.NotNull(toolset);
        var tools = await toolset.ListToolsAsync();
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
            return Task.FromResult<IToolset?>(ToolsetFactory.CreateFixedToolset(new WebRequestTool()));
        });

        var factory = ToolsetFactory.CreateNamedToolsetFactory(
            kind: "custom_kind",
            createToolsetAsync: (_, _) => Task.FromResult<IToolset?>(ToolsetFactory.CreateFixedToolset(new WebSearchTool())),
            underlyingInstance: underlying.Object);

        var toolset = await factory.CreateToolsetAsync(
            CreateCustomTool("other_kind"),
            new AgentServices());

        Assert.True(underlyingCalled);
        Assert.NotNull(toolset);
        var tools = await toolset.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("web_request", tools[0].Name);
    }

    [Fact]
    public async Task CreateWebSearchToolsetFactory_ReturnsWebSearchToolset()
    {
        var factory = ToolsetFactory.CreateWebSearchToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("web_search"), new AgentServices());

        Assert.NotNull(toolset);
        var tools = await toolset.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("web_search", tools[0].Name);
    }

    [Fact]
    public async Task CreateWebRequestToolsetFactory_ReturnsWebRequestToolset()
    {
        var factory = ToolsetFactory.CreateWebRequestToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("web_request"), new AgentServices());

        Assert.NotNull(toolset);
        var tools = await toolset.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("web_request", tools[0].Name);
    }

    [Fact]
    public async Task CreateWebToolsetFactory_ReturnsWebSearchAndWebRequestToolset()
    {
        var factory = ToolsetFactory.CreateWebToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("web"), new AgentServices());

        Assert.NotNull(toolset);
        var names = (await toolset.ListToolsAsync())
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

        Assert.IsType<FilesystemServiceToolset>(toolset);
    }

    [Fact]
    public async Task CreateDefaultToolsetFactory_ReturnsNullForUnknownKind()
    {
        var factory = ToolsetFactory.CreateDefaultToolsetFactory();
        var toolset = await factory.CreateToolsetAsync(CreateCustomTool("unknown_kind"), new AgentServices());

        Assert.Null(toolset);
    }

    [Fact]
    public async Task Combine_UsesFirstMatchingFactory()
    {
        var first = CreateMockToolsetFactory((tool, _) =>
            Task.FromResult<IToolset?>(
                tool.Kind == "one" ? ToolsetFactory.CreateFixedToolset(new WebSearchTool()) : null));
        var second = CreateMockToolsetFactory((tool, _) =>
            Task.FromResult<IToolset?>(
                tool.Kind == "two" ? ToolsetFactory.CreateFixedToolset(new WebRequestTool()) : null));
        var combined = ToolsetFactory.Combine(first.Object, second.Object);

        var oneToolset = await combined.CreateToolsetAsync(CreateCustomTool("one"), new AgentServices());
        var twoToolset = await combined.CreateToolsetAsync(CreateCustomTool("two"), new AgentServices());
        var missingToolset = await combined.CreateToolsetAsync(CreateCustomTool("three"), new AgentServices());

        Assert.NotNull(oneToolset);
        Assert.NotNull(twoToolset);
        Assert.Null(missingToolset);
        Assert.Equal("web_search", (await oneToolset.ListToolsAsync())[0].Name);
        Assert.Equal("web_request", (await twoToolset.ListToolsAsync())[0].Name);
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
        Func<Tool, AgentServices, Task<IToolset?>> createToolsetAsync)
    {
        var mock = new Mock<IToolsetFactory>();
        mock.Setup(factory => factory.CreateToolsetAsync(It.IsAny<Tool>(), It.IsAny<AgentServices>()))
            .Returns((Tool tool, AgentServices services) => createToolsetAsync(tool, services));
        return mock;
    }
}
