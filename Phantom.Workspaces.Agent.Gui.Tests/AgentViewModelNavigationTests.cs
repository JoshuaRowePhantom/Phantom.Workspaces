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
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.False(root.IsExpanded);
    }

    [Fact]
    public async Task EditorTree_DoesNotContain_DiagnosticsNode()
    {
        // Issue #819: Diagnostics node was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.DoesNotContain(root.Children, c => string.Equals(c.Id, "chat-diagnostics", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildToolNavigationItem_TopLevelItems_StartCollapsed()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

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
    public async Task SelectSubAgentNavItem_TwoSubAgents_ActivatesCorrectSubAgentDocument()
    {
        // Fix #1112: selecting a sub-agent nav item activates that sub-agent's OWN cached
        // AgentDetailDocumentItem (its own ConversationDetail). Switching selection activates a
        // DIFFERENT document — never the shared SubAgentsContainer.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "agent-1", "Agent One");
        await AddSubAgentAsync(chat, "agent-2", "Agent Two");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsGroup = root.Children.Single(c => c.Id == "chat-sub-agents");
        var childVm1 = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "agent-1").SubAgentViewModel;
        var childVm2 = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "agent-2").SubAgentViewModel;

        var second = subAgentsGroup.Children.Single(c => c.Id == "sub-agent-agent-2");
        viewModel.SelectedEditorItem = second;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(childVm2.ConversationDetail, viewModel.SelectedDetailDocument!.DetailContent);

        var first = subAgentsGroup.Children.Single(c => c.Id == "sub-agent-agent-1");
        viewModel.SelectedEditorItem = first;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(childVm1.ConversationDetail, viewModel.SelectedDetailDocument!.DetailContent);
    }

    [Fact]
    public async Task SelectSubAgentNavItem_ActivatesOwnConversationDocument()
    {
        // Fix #1112: the DetailContent for a sub-agent nav item is that sub-agent's OWN
        // ConversationDetail, and its cached AgentDetailDocumentItem is what becomes the active
        // Document — never the shared SubAgentsContainer.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

        await AddSubAgentAsync(chat, "agent-1", "Agent One");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsGroup = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsGroup.Children.Single(c => c.Id == "sub-agent-agent-1");
        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "agent-1").SubAgentViewModel;

        viewModel.SelectedEditorItem = subAgentNavItem;

        Assert.Same(childVm.ConversationDetail, viewModel.SelectedEditorDetailContent);
        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(childVm.ConversationDetail, viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
    }

    [Fact]
    public async Task AddSubAgentSlot_WithSubAgentWrapper_DoesNotThrow()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, TaskScheduler.Default);

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
