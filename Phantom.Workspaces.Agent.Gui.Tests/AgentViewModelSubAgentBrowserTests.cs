using AgentSchema;
using System.Collections.Specialized;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSubAgentBrowserTests
{
    // ── §10 Sub-agents group node ─────────────────────────────────────────────

    [Fact]
    public async Task SubAgentsGroup_AppearsInEditorTree_WhenSubAgentsExist()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Agent Alpha");

        var root = Assert.Single(viewModel.EditorItems);
        Assert.Contains(root.Children, c => c.Id == "chat-sub-agents");
    }

    [Fact]
    public async Task SubAgentsGroup_Count_ReflectsSubAgentsCount()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Alpha");
        await AddSubAgentAsync(chat, "a2", "Beta");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        Assert.Equal("Sub-agents (2)", subAgentsNode.Name);
    }

    // ── §10 Browser card ──────────────────────────────────────────────────────

    [Fact]
    public void BrowserCard_SortedReverseChronologically_ByLastUpdatedAt()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var source = new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>
        {
            new StubSubAgentItem("first", "First Agent", AgentChatCompletionState.Running, t0),
            new StubSubAgentItem("second", "Second Agent", AgentChatCompletionState.Running, t1),
        };
        var all = new System.Collections.ObjectModel.ReadOnlyObservableCollection<IRunningSubAgent>(source);
        using var browser = new SubAgentBrowserViewModel(all);

        // Most recently updated should appear first.
        Assert.Collection(browser.VisibleItems,
            item => Assert.Equal("second", item.AgentId),
            item => Assert.Equal("first", item.AgentId));
    }

    [Fact]
    public async Task HideCompleted_True_ShowsOnlyRunningSubAgents()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        await AddSubAgentAsync(chat, "running", "Running Agent");
        await AddSubAgentAsync(chat, "done", "Done Agent");

        // Mark "done" as succeeded.
        var doneChat = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "done");
        doneChat.SetCompletionState(AgentChatCompletionState.Succeeded);

        viewModel.SubAgentsContainer.Browser.HideCompleted = true;

        var items = viewModel.SubAgentsContainer.Browser.VisibleItems;
        Assert.Single(items);
        Assert.Equal("running", items[0].AgentId);
    }

    [Fact]
    public async Task HideCompleted_False_ShowsAllSubAgents()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        await AddSubAgentAsync(chat, "running", "Running");
        var subAgentChat = (AgentChat)chat.SubAgents.Single(s => s.AgentId == "running");
        subAgentChat.SetCompletionState(AgentChatCompletionState.Succeeded);

        viewModel.SubAgentsContainer.Browser.HideCompleted = false;

        Assert.Single(viewModel.SubAgentsContainer.Browser.VisibleItems);
    }

    // ── §13 NavigateToAgent ───────────────────────────────────────────────────

    [Fact]
    public async Task NavigateToAgent_OpensMatchingSubAgentView()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub A");
        await AddSubAgentAsync(chat, "a2", "Sub B");

        viewModel.NavigateToAgentHandler!.Invoke("a2");

        // The container should show sub-agent "a2", not the browser.
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);
        var selectedSlot = viewModel.SubAgentsContainer.Slots.Single(s => s.IsSelected);
        Assert.Equal("a2", selectedSlot.AgentId);
    }

    // ── §5 AcceptsUserInput ───────────────────────────────────────────────────

    [Fact]
    public async Task QueueComposerControl_Hidden_WhenAcceptsUserInput_False()
    {
        // Sub-agents use IHostedAgentChatClient → AcceptsUserInput is false.
        var chat = await CreateChatAsync();
        await AddSubAgentAsync(chat, "sub1", "Sub Agent");

        var subAgentEntry = chat.SubAgents.Single(s => s.AgentId == "sub1");
        var subAgentChat = (AgentChat)subAgentEntry;

        using var loggerFactory = new ObservableLoggerFactory();
        await using var subAgentViewModel = new AgentViewModel(subAgentChat, "sub", loggerFactory);

        Assert.False(subAgentViewModel.AcceptsUserInput);
    }

    [Fact]
    public async Task QueueComposerControl_Visible_WhenAcceptsUserInput_True()
    {
        // Root/parent agents use a real chat client → AcceptsUserInput is true.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        Assert.True(viewModel.AcceptsUserInput);
    }

    // ── §11 View retention ────────────────────────────────────────────────────

    [Fact]
    public async Task SubAgentView_ContextSwitch_DoesNotDisposeAndRecreateControl()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub A");
        await AddSubAgentAsync(chat, "a2", "Sub B");

        // Navigate to sub-agent A.
        viewModel.NavigateToAgentHandler!.Invoke("a1");
        var slotA = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1");
        var vmA = slotA.SubAgentViewModel;

        // Navigate to sub-agent B (away from A).
        viewModel.NavigateToAgentHandler.Invoke("a2");

        // Navigate back to A.
        viewModel.NavigateToAgentHandler.Invoke("a1");
        var slotAAfter = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1");
        var vmAAfter = slotAAfter.SubAgentViewModel;

        // Same slot instance, same view model instance — the control is not recreated.
        Assert.Same(slotA, slotAAfter);
        Assert.Same(vmA, vmAAfter);
    }

    // Helpers ───────────────────────────────────────────────────────────────

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

    private static Task<AgentChat> CreateChatAsync()
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = CreateAgentDefinition(),
            });

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

    private sealed class StubSubAgentItem(
        string agentId,
        string displayName,
        AgentChatCompletionState completionState,
        DateTime lastUpdatedAt) : IRunningSubAgent
    {
        public string AgentId { get; } = agentId;
        public string DisplayName { get; } = displayName;
        public AgentChatCompletionState CompletionState { get; } = completionState;
        public DateTime LastUpdatedAt { get; } = lastUpdatedAt;
        public IReadOnlyList<IRunningSubAgent> SubAgents => [];
    }
}
