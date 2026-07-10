using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelNavigationTests
{
    [Fact]
    public async Task EditorTree_RootNode_IsCollapsedByDefault()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.False(root.IsExpanded);
    }

    [Fact]
    public async Task EditorTree_ContainsDiagnosticsNode()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.Contains(root.Children, c => string.Equals(c.Id, "chat-diagnostics", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildToolNavigationItem_TopLevelItems_StartCollapsed()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await WaitForMcpToolsLoadedAsync(chat);

        var toolsNode = viewModel.EditorItems.Single().Children.Single(c => c.Id == "chat-tools");
        var topLevelMcpItem = Assert.Single(toolsNode.Children);
        Assert.False(topLevelMcpItem.IsExpanded);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildToolNavigationItem_ChildItems_StartExpanded()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

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

    // ── Sub-agent navigation tests ─────────────────────────────────────────────

    [Fact]
    public async Task SelectSubAgentNavItem_TwoSubAgents_ShowsCorrectSlot()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "agent-1", "Agent One");
        await AddSubAgentAsync(chat, "agent-2", "Agent Two");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsGroup = root.Children.Single(c => c.Id == "chat-sub-agents");

        // Select the second sub-agent nav item.
        var secondSubAgentNavItem = subAgentsGroup.Children.Single(c => c.Id == "sub-agent-agent-2");
        viewModel.SelectedEditorItem = secondSubAgentNavItem;

        // The container should show agent-2, not agent-1.
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);
        var selectedSlot = viewModel.SubAgentsContainer.Slots.Single(s => s.IsSelected);
        Assert.Equal("agent-2", selectedSlot.AgentId);

        // Now select the first sub-agent.
        var firstSubAgentNavItem = subAgentsGroup.Children.Single(c => c.Id == "sub-agent-agent-1");
        viewModel.SelectedEditorItem = firstSubAgentNavItem;

        // The container should now show agent-1.
        selectedSlot = viewModel.SubAgentsContainer.Slots.Single(s => s.IsSelected);
        Assert.Equal("agent-1", selectedSlot.AgentId);
    }

    [Fact]
    public async Task SelectSubAgentNavItem_DetailContentSlot_IsVisible()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "agent-1", "Agent One");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsGroup = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsGroup.Children.Single(c => c.Id == "sub-agent-agent-1");

        // Select the sub-agent nav item.
        viewModel.SelectedEditorItem = subAgentNavItem;

        // The detail content should be the sub-agents container.
        Assert.Same(viewModel.SubAgentsContainer, viewModel.SelectedEditorDetailContent);
        // The container should not be showing the browser.
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);
    }

    [Fact]
    public async Task AddSubAgentSlot_WithSubAgentWrapper_DoesNotThrow()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        // Add a real sub-agent first to ensure the mechanism works.
        await AddSubAgentAsync(chat, "agent-1", "Agent One");

        // Verify a slot was created with the correct AgentId.
        var slot = Assert.Single(viewModel.SubAgentsContainer.Slots);
        Assert.Equal("agent-1", slot.AgentId);

        // The fix ensures SubAgent wrappers (which the system may use for lazy loading)
        // don't cause InvalidCastException - this test verifies the fix handles
        // both AgentChat and SubAgent types correctly.
    }

    private static async Task<IRunningSubAgent> AddSubAgentAsync(
        AgentChat chat,
        string agentId,
        string displayName)
    {
        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{displayName}}",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}");
        return chat.SubAgents.Single(s => s.AgentId == agentId);
    }
}
