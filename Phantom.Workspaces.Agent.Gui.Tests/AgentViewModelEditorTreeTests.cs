using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelEditorTreeTests
{
    [Fact]
    public async Task AgentViewModel_DoesNotExpose_DiagnosticsInspectorProperty()
    {
        // Issue #819: The DiagnosticsInspector property was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var property = typeof(AgentViewModel).GetProperty("DiagnosticsInspector");
        Assert.Null(property);
    }

    [Fact]
    public async Task AgentViewModel_DoesNotContain_DiagnosticsNavigationItem()
    {
        // Issue #819: The Diagnostics navigation item (tab) was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var diagnosticsNav = root.Children.FirstOrDefault(c => c.Id == "chat-diagnostics");
        Assert.Null(diagnosticsNav);
    }

    [Fact]
    public async Task ToolsNavItem_IdentityStable_AfterToolsChanged()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var toolsNavBefore = root.Children.First(c => c.Id == "chat-tools");

        chat.RaiseToolsChanged();

        var toolsNavAfter = root.Children.First(c => c.Id == "chat-tools");
        Assert.Same(toolsNavBefore, toolsNavAfter);
    }

    [Fact]
    public async Task RootNavItem_IdentityStable_AfterSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var rootBefore = Assert.Single(viewModel.EditorItems);
        var chatDetailsBefore = rootBefore.Children.First(c => c.Id == "chat-details");
        var toolsBefore = rootBefore.Children.First(c => c.Id == "chat-tools");

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var rootAfter = Assert.Single(viewModel.EditorItems);
        var chatDetailsAfter = rootAfter.Children.First(c => c.Id == "chat-details");
        var toolsAfter = rootAfter.Children.First(c => c.Id == "chat-tools");

        Assert.Same(rootBefore, rootAfter);
        Assert.Same(chatDetailsBefore, chatDetailsAfter);
        Assert.Same(toolsBefore, toolsAfter);
    }

    [Fact]
    public async Task ToolsNavItem_ChildrenUpdate_Incrementally()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var chat = await CreateChatWithMcpAsync(server.BoundUrl);
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var toolsNav = root.Children.First(c => c.Id == "chat-tools");
        var chatDetailsNav = root.Children.First(c => c.Id == "chat-details");
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        await WaitForMcpToolsLoadedAsync(chat);

        Assert.Single(toolsNav.Children);
        Assert.Empty(chatDetailsNav.Children);
        Assert.Empty(subAgentsNav.Children);
    }

    [Fact]
    public async Task SubAgentsNavItem_ChildrenUpdate_Incrementally()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var toolsNav = root.Children.First(c => c.Id == "chat-tools");

        Assert.Empty(subAgentsNav.Children);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        Assert.Single(subAgentsNav.Children);
        Assert.Empty(toolsNav.Children);
    }

    [Fact]
    public async Task SubAgentsNavItem_Name_UpdatesCount_OnSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        Assert.Equal("Sub-agents", subAgentsNav.Name);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        Assert.Equal("Sub-agents (1)", subAgentsNav.Name);

        await AddSubAgentAsync(chat, "sub-agent-2", "Sub Agent 2");

        Assert.Equal("Sub-agents (2)", subAgentsNav.Name);
    }

    [Fact]
    public async Task ToolsNavItem_IsExpanded_Retained_AfterToolsChange()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var toolsNav = root.Children.First(c => c.Id == "chat-tools");

        Assert.True(toolsNav.IsExpanded);

        toolsNav.IsExpanded = false;
        chat.RaiseToolsChanged();

        Assert.False(toolsNav.IsExpanded);
    }

    [Fact]
    public async Task SelectedEditorItem_Retained_AfterToolsChange()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        // Issue #819: Use chat-details instead of removed chat-diagnostics
        var chatDetailsNav = root.Children.First(c => c.Id == "chat-details");

        viewModel.SelectedEditorItem = chatDetailsNav;

        chat.RaiseToolsChanged();

        Assert.Same(chatDetailsNav, viewModel.SelectedEditorItem);
    }

    [Fact]
    public async Task SubAgentNavItem_HasChatDetailsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-details");
    }

    [Fact]
    public async Task SubAgentNavItem_HasToolsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-tools");
    }

    [Fact]
    public async Task SubAgentNavItem_DoesNotHaveBackgroundTasksChild()
    {
        // Issue #1030: The "Background tasks" section was removed from every sub-agent too.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.DoesNotContain(subAgentNav.Children, c => c.Id == "chat-background-tasks");
    }

    [Fact]
    public async Task EditorItems_DoesNotContainBackgroundTasksSection()
    {
        // Issue #1030: The "Background tasks" placeholder section was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.DoesNotContain(root.Children, c => c.Id == "chat-background-tasks");
    }

    [Fact]
    public async Task EditorItems_RootChildren_AreChatDetailsToolsSubAgents()
    {
        // Issue #1030: The root nav item's children are exactly Chat details, Tools, Sub-agents.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        Assert.Equal(
            new[] { "chat-details", "chat-tools", "chat-sub-agents" },
            root.Children.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task SubAgentNavItem_HasSubAgentsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-sub-agents");
    }

    [Fact]
    public async Task SubAgentNavItem_DoesNotHave_DiagnosticsChild()
    {
        // Issue #819: Diagnostics tab was removed.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.DoesNotContain(subAgentNav.Children, c => c.Id == "chat-diagnostics");
    }

    [Fact]
    public async Task SubAgentNavItem_DetailContent_IsSubAgentsContainerDetail()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.NotNull(subAgentNav.DetailContent);
        Assert.Same(viewModel.SubAgentsContainer, subAgentNav.DetailContent);
    }

    [Fact]
    public async Task SubAgentNavItem_Children_LiveUpdate_OnToolsChange()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var subAgentDef = CreateMcpAgentDefinition(server.BoundUrl);
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await chat.GetOrCreateAsync("sub-agent-1", subAgentDef, "tool-call-sub-agent-1", TestContext.Current.CancellationToken);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);
        var subAgentToolsNav = subAgentNav.Children.First(c => c.Id == "chat-tools");

        var subAgentChat = (AgentChat)chat.SubAgents.First(s => s.AgentId == "sub-agent-1");
        await WaitForMcpToolsLoadedAsync(subAgentChat);

        Assert.Single(subAgentToolsNav.Children);
    }

    [Fact]
    public async Task SubAgentNavItem_Recursive_HasGrandchildStructure()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        var subAgent = (AgentChat)chat.SubAgents.First();
        await AddSubAgentAsync(subAgent, "sub-sub-agent", "Sub Sub Agent");

        var subAgentSubAgentsNav = subAgentNav.Children.First(c => c.Id == "chat-sub-agents");

        var grandchildNav = Assert.Single(subAgentSubAgentsNav.Children);
        // Issue #819 removed Diagnostics; issue #1030 removed Background tasks. Count is now 3.
        Assert.Equal(3, grandchildNav.Children.Count);
    }

    [Fact]
    public async Task DetailContentSlots_AllFixedSlotsPresent_OnConstruction()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        // Issue #819 removed Diagnostics; issue #1030 removed Background tasks. Count is now 4.
        Assert.Equal(4, viewModel.DetailContentSlots.Count);
    }

    [Fact]
    public async Task DetailContentSlots_OnlySelectedSlotVisible()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        // Issue #819: Use chat-details instead of removed chat-diagnostics
        var chatDetailsNav = root.Children.First(c => c.Id == "chat-details");

        viewModel.SelectedEditorItem = chatDetailsNav;

        var visibleSlots = viewModel.DetailContentSlots.Where(s => s.IsVisible).ToList();
        var slot = Assert.Single(visibleSlots);
        Assert.Same(chatDetailsNav.DetailContent, slot.Content);
    }

    [Fact]
    public async Task DetailContentSlots_ConversationSlot_RemainsAlive_AfterNavigation()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var conversationSlot = viewModel.DetailContentSlots
            .First(s => s.Content is AgentChatConversationDetailViewModel);
        var conversationContent = conversationSlot.Content;

        viewModel.SelectedEditorItem = root.Children.First(c => c.Id == "chat-details");
        viewModel.SelectedEditorItem = root;

        Assert.Same(conversationContent, conversationSlot.Content);
    }

    [Fact]
    public async Task DetailContentSlots_VisibilityToggles_ContentIdentityStable()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var slotContents = viewModel.DetailContentSlots.Select(s => s.Content).ToArray();

        viewModel.SelectedEditorItem = root.Children.First(c => c.Id == "chat-details");
        viewModel.SelectedEditorItem = root.Children.First(c => c.Id == "chat-tools");
        viewModel.SelectedEditorItem = root;

        var slotContentsAfter = viewModel.DetailContentSlots.Select(s => s.Content).ToArray();

        for (int i = 0; i < slotContents.Length; i++)
        {
            Assert.Same(slotContents[i], slotContentsAfter[i]);
        }
    }

    [Fact]
    public async Task DetailContentSlots_ConversationSlotVisible_OnConstruction()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var conversationSlot = viewModel.DetailContentSlots
            .First(s => s.Content is AgentChatConversationDetailViewModel);

        Assert.True(conversationSlot.IsVisible);
    }

    [Fact]
    public async Task SubAgentsNavItem_AutoExpands_WhenFirstSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        Assert.False(subAgentsNav.IsExpanded);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        Assert.True(subAgentsNav.IsExpanded);
    }

    [Fact]
    public async Task SubAgentsNavItem_RemainsExpanded_WhenSecondSubAgentAdded()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");
        Assert.True(subAgentsNav.IsExpanded);

        await AddSubAgentAsync(chat, "sub-agent-2", "Sub Agent 2");
        Assert.True(subAgentsNav.IsExpanded);
    }

    [Fact]
    public async Task AgentViewModel_ConversationDetail_ReturnsSameInstanceUsedByEditorTree()
    {
        // Issue #903: Verify that the ConversationDetail property exposes the same instance
        // that is used by the editor tree, so the SubAgentSlotViewModel DataTemplate can bind
        // directly to it without instantiating a nested AgentChatEditorControl.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory);

        var conversationDetail = viewModel.ConversationDetail;
        Assert.NotNull(conversationDetail);

        var root = Assert.Single(viewModel.EditorItems);
        var rootDetailContent = root.DetailContent;

        Assert.Same(conversationDetail, rootDetailContent);
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

    private static async Task AddSubAgentAsync(AgentChat chat, string agentId, string displayName)
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

        await chat.GetOrCreateAsync(agentId, definition, $"tool-call-{agentId}", TestContext.Current.CancellationToken);
    }

    private static async Task WaitForMcpToolsLoadedAsync(AgentChat chat)
    {
        if (chat.GetToolSnapshot().Count > 0)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnToolsChanged(object? _, EventArgs __)
        {
            if (chat.GetToolSnapshot().Count > 0)
            {
                signal.TrySetResult();
            }
        }

        chat.ToolsChanged += OnToolsChanged;
        try
        {
            if (chat.GetToolSnapshot().Count > 0)
            {
                return;
            }

            const int timeoutMs = 30_000;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(timeoutMs);

            try
            {
                await signal.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"MCP tools did not load within {timeoutMs}ms");
            }
        }
        finally
        {
            chat.ToolsChanged -= OnToolsChanged;
        }
    }
}
