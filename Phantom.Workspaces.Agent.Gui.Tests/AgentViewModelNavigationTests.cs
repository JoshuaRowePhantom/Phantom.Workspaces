using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelNavigationTests
{
    [AvaloniaFact]
    public async Task EditorTree_RootNode_IsCollapsedByDefault()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.False(root.IsExpanded);
    }

    [AvaloniaFact]
    public async Task BuildToolNavigationItem_TopLevelItems_StartCollapsed()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await WaitForMcpToolsLoadedAsync(chat);

        var toolsNode = viewModel.EditorItems.Single().Children.Single(c => c.Id == "chat-tools");
        var topLevelMcpItem = Assert.Single(toolsNode.Children);
        Assert.False(topLevelMcpItem.IsExpanded);
    }

    [AvaloniaFact]
    public async Task BuildToolNavigationItem_ChildItems_StartExpanded()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await WaitForMcpToolsLoadedAsync(chat);

        var toolsNode = viewModel.EditorItems.Single().Children.Single(c => c.Id == "chat-tools");
        var topLevelMcpItem = Assert.Single(toolsNode.Children);
        var childToolItem = Assert.Single(topLevelMcpItem.Children);
        Assert.True(childToolItem.IsExpanded);
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    private static AgentDefinition CreateMcpAgentDefinition(string endpoint)
        => AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                {
                  "kind": "mcp",
                  "name": "test-mcp",
                  "serverName": "test-mcp",
                  "connection": {
                    "kind": "Anonymous",
                    "endpoint": "{{endpoint}}"
                  }
                }
              ]
            }
            """);

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

    private static Task<AgentChat> CreateChatWithMcpAsync(string mcpEndpoint)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateMcpAgentDefinition(mcpEndpoint),
            });

    private static async Task WaitForMcpToolsLoadedAsync(AgentChat chat)
    {
        if (HasChildTools(chat))
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnToolsChanged(object? _, EventArgs __)
        {
            if (HasChildTools(chat))
            {
                signal.TrySetResult();
            }
        }

        chat.ToolsChanged += OnToolsChanged;
        try
        {
            if (HasChildTools(chat))
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            chat.ToolsChanged -= OnToolsChanged;
        }
    }

    private static bool HasChildTools(AgentChat chat)
        => chat.GetToolSnapshot().Any(t => t.Children.Count > 0);
}
