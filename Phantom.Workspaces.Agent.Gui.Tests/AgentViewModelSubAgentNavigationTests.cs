using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSubAgentNavigationTests
{
    [Fact]
    public async Task SubAgentNavItem_DetailContent_IsSubAgentsContainerDetail()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = Assert.Single(subAgentsNode.Children);

        Assert.Same(viewModel.SubAgentsContainer, subAgentNavItem.DetailContent);
    }

    [Fact]
    public async Task SelectSubAgentNavItem_CallsShowSubAgent_WithCorrectAgentId()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");

        viewModel.SelectedEditorItem = subAgentNavItem;

        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);
        var selectedSlot = viewModel.SubAgentsContainer.Slots.Single(s => s.IsSelected);
        Assert.Equal("a1", selectedSlot.AgentId);
    }

    [Fact]
    public async Task SelectSubAgentsContainerNavItem_CallsShowBrowser()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = Assert.Single(subAgentsNode.Children);

        viewModel.SelectedEditorItem = subAgentNavItem;
        Assert.False(viewModel.SubAgentsContainer.IsShowingBrowser);

        viewModel.SelectedEditorItem = subAgentsNode;

        Assert.True(viewModel.SubAgentsContainer.IsShowingBrowser);
    }

    [Fact]
    public async Task SubAgentSlot_IsSelected_WhenSubAgentNavItemSelected()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a2");

        viewModel.SelectedEditorItem = subAgentNavItem;

        var slot = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a2");
        Assert.True(slot.IsSelected);
    }

    [Fact]
    public async Task SubAgentSlot_IsSelected_False_WhenOtherSubAgentNavItemSelected()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItemA = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");

        viewModel.SelectedEditorItem = subAgentNavItemA;

        var slotB = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a2");
        Assert.False(slotB.IsSelected);
    }

    [Fact]
    public async Task SubAgentsContainerSlot_IsVisible_WhenSubAgentNavItemSelected()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = Assert.Single(subAgentsNode.Children);

        viewModel.SelectedEditorItem = subAgentNavItem;

        var containerSlot = viewModel.DetailContentSlots.Single(s => 
            ReferenceEquals(s.Content, viewModel.SubAgentsContainer));
        Assert.True(containerSlot.IsVisible);
    }

    [Fact]
    public async Task AgentViewModel_WithParentAgent_ParentAgentViewModelIsNotNull()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;

        Assert.NotNull(childVm.ParentAgentViewModel);
        Assert.Same(viewModel, childVm.ParentAgentViewModel);
    }

    [Fact]
    public async Task AgentViewModel_WithNoParentAgent_ParentAgentViewModelIsNull()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        Assert.Null(viewModel.ParentAgentViewModel);
    }

    [Fact]
    public async Task NavigateToAgent_ParentAgentId_NavigatesToParentView()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;

        // Navigate into the sub-agent first so we're not already on the parent view.
        viewModel.NavigateToAgentHandler!.Invoke("a1");

        // Navigate to the parent's session id — the id carried by the [Parent agent] link.
        childVm.NavigateToAgent(chat.AgentSessionId);

        Assert.NotNull(viewModel.SelectedEditorItem);
        Assert.Equal(viewModel.EditorItems[0], viewModel.SelectedEditorItem);
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

    [Fact]
    public async Task SubAgentNavItem_DisplaysTwoLines_NameAndDescription()
    {
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        var definition = AgentDefinitionLoader.LoadAgentFromJson(
            """
            {
              "kind": "prompt",
              "name": "test-subagent",
              "description": "A description for the subagent",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

        await chat.GetOrCreateAsync("sa1", definition, "tool-call-sa1", TestContext.Current.CancellationToken);

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = Assert.Single(subAgentsNode.Children);

        Assert.Equal("test-subagent", subAgentNavItem.Name);
        Assert.Equal("A description for the subagent", subAgentNavItem.Summary);
    }
}
