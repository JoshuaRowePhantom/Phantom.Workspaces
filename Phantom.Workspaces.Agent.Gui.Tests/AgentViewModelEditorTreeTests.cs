using AgentSchema;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelEditorTreeTests
{
    [Fact]
    public async Task ToolsNavItem_IdentityStable_AfterToolsChanged()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var diagnosticsNav = root.Children.First(c => c.Id == "chat-diagnostics");

        viewModel.SelectedEditorItem = diagnosticsNav;

        chat.RaiseToolsChanged();

        Assert.Same(diagnosticsNav, viewModel.SelectedEditorItem);
    }

    [Fact]
    public async Task SubAgentNavItem_HasChatDetailsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-tools");
    }

    [Fact]
    public async Task SubAgentNavItem_HasBackgroundTasksChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-background-tasks");
    }

    [Fact]
    public async Task SubAgentNavItem_HasSubAgentsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-sub-agents");
    }

    [Fact]
    public async Task SubAgentNavItem_HasDiagnosticsChild()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.Contains(subAgentNav.Children, c => c.Id == "chat-diagnostics");
    }

    [Fact]
    public async Task SubAgentNavItem_DetailContent_IsConversationDetail()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        Assert.NotNull(subAgentNav.DetailContent);
        Assert.IsType<AgentChatConversationDetailViewModel>(subAgentNav.DetailContent);
    }

    [Fact]
    public async Task SubAgentNavItem_Children_LiveUpdate_OnToolsChange()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var subAgentDef = CreateMcpAgentDefinition(server.BoundUrl);
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        await AddSubAgentAsync(chat, "sub-agent-1", "Sub Agent 1");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNav = root.Children.First(c => c.Id == "chat-sub-agents");
        var subAgentNav = Assert.Single(subAgentsNav.Children);

        var subAgent = (AgentChat)chat.SubAgents.First();
        await AddSubAgentAsync(subAgent, "sub-sub-agent", "Sub Sub Agent");

        var subAgentSubAgentsNav = subAgentNav.Children.First(c => c.Id == "chat-sub-agents");

        var grandchildNav = Assert.Single(subAgentSubAgentsNav.Children);
        Assert.Equal(5, grandchildNav.Children.Count);
    }

    [Fact]
    public async Task DetailContentSlots_AllFixedSlotsPresent_OnConstruction()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        Assert.Equal(6, viewModel.DetailContentSlots.Count);
    }

    [Fact]
    public async Task DetailContentSlots_OnlySelectedSlotVisible()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        var root = Assert.Single(viewModel.EditorItems);
        var diagnosticsNav = root.Children.First(c => c.Id == "chat-diagnostics");

        viewModel.SelectedEditorItem = diagnosticsNav;

        var visibleSlots = viewModel.DetailContentSlots.Where(s => s.IsVisible).ToList();
        var slot = Assert.Single(visibleSlots);
        Assert.Same(diagnosticsNav.DetailContent, slot.Content);
    }

    [Fact]
    public async Task DetailContentSlots_ConversationSlot_RemainsAlive_AfterNavigation()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

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
        await using var viewModel = new AgentViewModel(chat, "test-agent", loggerFactory);

        var conversationSlot = viewModel.DetailContentSlots
            .First(s => s.Content is AgentChatConversationDetailViewModel);

        Assert.True(conversationSlot.IsVisible);
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
        const int timeoutMs = 30_000;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeoutMs);
        while (chat.GetToolSnapshot().Count == 0 && !cts.Token.IsCancellationRequested)
        {
            await Task.Delay(100, cts.Token);
        }

        if (cts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP tools did not load within {timeoutMs}ms");
        }
    }
}
