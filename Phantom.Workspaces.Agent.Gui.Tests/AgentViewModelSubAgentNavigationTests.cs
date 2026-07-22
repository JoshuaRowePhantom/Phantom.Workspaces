using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelSubAgentNavigationTests
{
    [Fact]
    public async Task SubAgentNavItem_DetailContent_IsOwnConversationDetail()
    {
        // Fix #1112: each sub-agent's nav item DetailContent must be that sub-agent's OWN
        // ConversationDetail (not the shared SubAgentsContainer) so the SelectedEditorItem scan
        // resolves each sub-agent to a distinct AgentDetailDocumentItem, and only ONE
        // AgentChatOutputControl/WebView2 is realised at a time.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = Assert.Single(subAgentsNode.Children);
        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;

        Assert.Same(childVm.ConversationDetail, subAgentNavItem.DetailContent);
        Assert.NotSame(viewModel.SubAgentsContainer, subAgentNavItem.DetailContent);
    }

    [Fact]
    public async Task SelectSubAgentNavItem_ActivatesThatSubAgentsOwnDocument()
    {
        // Fix #1112: selecting a sub-agent nav item activates the sub-agent's OWN cached Document
        // (the one carrying its ConversationDetail), not the shared sub-agents-container document.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;

        viewModel.SelectedEditorItem = subAgentNavItem;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(childVm.ConversationDetail, viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
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
        // Group node still shows the browser card when selected.
        viewModel.SelectedEditorItem = subAgentsNode;

        Assert.True(viewModel.SubAgentsContainer.IsShowingBrowser);
    }

    [Fact]
    public async Task SelectSubAgent_SwitchSelection_ActiveDockableChangesToNewAgent()
    {
        // Fix #1112: switching selection from one sub-agent to another changes the active Document
        // to the newly selected sub-agent's own Document (distinct documents per sub-agent).
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var navA = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var navB = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a2");
        var childVmA = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;
        var childVmB = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a2").SubAgentViewModel;

        viewModel.SelectedEditorItem = navA;
        var activeA = viewModel.DetailDockFactory.ActiveDocument;

        viewModel.SelectedEditorItem = navB;
        var activeB = viewModel.DetailDockFactory.ActiveDocument;

        Assert.NotNull(activeA);
        Assert.NotNull(activeB);
        Assert.NotSame(activeA, activeB);
        Assert.Same(childVmA.ConversationDetail, activeA!.DetailContent);
        Assert.Same(childVmB.ConversationDetail, activeB!.DetailContent);
    }

    [Fact]
    public async Task EachSubAgent_MapsToDistinctDetailDocument()
    {
        // Fix #1112: distinct sub-agents map to DIFFERENT AgentDetailDocumentItems in
        // AllDetailContents — never share one document that would airspace-overlap their transcripts.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var childVmA = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;
        var childVmB = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a2").SubAgentViewModel;

        var docA = viewModel.AllDetailContents.Single(i => ReferenceEquals(i.Content, childVmA.ConversationDetail));
        var docB = viewModel.AllDetailContents.Single(i => ReferenceEquals(i.Content, childVmB.ConversationDetail));

        Assert.NotSame(docA, docB);
        Assert.NotEqual(docA.Key, docB.Key);
    }

    [Fact]
    public async Task OnlyOneDetailDocument_IsActive_AtATime()
    {
        // Fix #1112: the shared DocumentDock keeps exactly one active document, guaranteeing at most
        // one native transcript surface (WebView2) is materialised even with several sub-agents.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent A");
        await AddSubAgentAsync(chat, "a2", "Sub Agent B");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");

        foreach (var id in new[] { "sub-agent-a1", "sub-agent-a2" })
        {
            viewModel.SelectedEditorItem = subAgentsNode.Children.Single(c => c.Id == id);
            var active = viewModel.DetailDockFactory.ActiveDocument;
            Assert.NotNull(active);
            Assert.Same(viewModel.SelectedDetailDocument, active);
        }
    }

    [Fact]
    public async Task SubAgentsContainerDocument_IsActive_WhenSubAgentsGroupNodeSelected()
    {
        // Fix #1112: the shared SubAgentsContainer document is activated ONLY when the group
        // "Sub-agents (N)" nav item is selected (for the browser card view).
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");

        viewModel.SelectedEditorItem = subAgentsNode;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(viewModel.SubAgentsContainer, viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
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

    [Fact]
    public async Task SelectSubAgentChatDetailsChild_RendersNonBlankDetail()
    {
        // Issue #1035 regression: selecting a sub-agent's own chat-details child node must resolve
        // to a real cached document (never blank).
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var subChatDetails = subAgentNavItem.Children.Single(c => c.Id == "chat-details");

        viewModel.SelectedEditorItem = subChatDetails;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(subChatDetails.DetailContent, viewModel.SelectedDetailDocument!.DetailContent);
        Assert.IsType<AgentChatDetailsViewModel>(viewModel.SelectedDetailDocument.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
    }

    [Fact]
    public async Task SubAgentChatDetails_ShowsSubAgentModelAndSession_NotParent()
    {
        // Issue #1035 regression: the sub-agent's chat-details detail describes the SUB-AGENT, not
        // the parent — its backing AgentViewModel is the sub-agent's own view-model.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var subChatDetails = subAgentNavItem.Children.Single(c => c.Id == "chat-details");

        viewModel.SelectedEditorItem = subChatDetails;

        var details = Assert.IsType<AgentChatDetailsViewModel>(viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(childVm, details.Agent);

        // It must NOT be the parent's own chat-details detail.
        var parentChatDetails = root.Children.Single(c => c.Id == "chat-details");
        Assert.NotSame(parentChatDetails.DetailContent, viewModel.SelectedDetailDocument.DetailContent);
    }

    [Fact]
    public async Task SelectSubAgentToolsChild_RendersNonBlankDetail()
    {
        // Issue #1035 regression: selecting a sub-agent's own tools child node resolves to a cached document.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var subTools = subAgentNavItem.Children.Single(c => c.Id == "chat-tools");

        viewModel.SelectedEditorItem = subTools;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(subTools.DetailContent, viewModel.SelectedDetailDocument!.DetailContent);
        Assert.Same(viewModel.SelectedDetailDocument, viewModel.DetailDockFactory.ActiveDocument);
    }

    [Fact]
    public async Task ActiveDetailDocument_Tracks_TreeSelection()
    {
        // Issue #1035: the dock's active document always follows the selected nav node.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var subChatDetails = subAgentNavItem.Children.Single(c => c.Id == "chat-details");

        foreach (var node in new[]
        {
            root,
            root.Children.Single(c => c.Id == "chat-details"),
            root.Children.Single(c => c.Id == "chat-tools"),
            subChatDetails,
        })
        {
            viewModel.SelectedEditorItem = node;
            var active = viewModel.DetailDockFactory.ActiveDocument;
            Assert.NotNull(active);
            Assert.Same(node.DetailContent, active!.DetailContent);
        }
    }

    [Fact]
    public async Task AgentViewModel_SubAgentAdded_AppendsDocumentToSharedCollection()
    {
        // Issue #1035: adding a sub-agent appends its detail items to the shared AllDetailContents,
        // each with a generated cached document.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        var beforeCount = viewModel.AllDetailContents.Count;

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        Assert.True(viewModel.AllDetailContents.Count > beforeCount);

        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;
        foreach (var item in childVm.AllDetailContents)
        {
            Assert.Contains(item, viewModel.AllDetailContents);
            Assert.NotNull(viewModel.DetailDockFactory.GetDocument(item));
        }
    }

    [Fact]
    public async Task AllDetailContents_Flattens_NestedSubAgentDetailVMs()
    {
        // Issue #1035: arbitrarily nested sub-agent detail VMs are flattened into the root collection.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");
        var childVm = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").SubAgentViewModel;

        // Add a grandchild sub-agent beneath the first sub-agent.
        var childChat = (AgentChat)viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1").RunningSubAgent;
        await AddSubAgentAsync(childChat, "a1-1", "Grandchild Agent");

        var grandchildVm = childVm.SubAgentsContainer.Slots.Single(s => s.AgentId == "a1-1").SubAgentViewModel;

        foreach (var item in grandchildVm.AllDetailContents)
        {
            Assert.Contains(item, viewModel.AllDetailContents);
        }
    }

    [Fact]
    public async Task AgentViewModel_SubAgentCompleted_UpdatesCollectionWithoutBlankPanel()
    {
        // Issue #1035: after a sub-agent completes, its chat-details child still resolves to a document.
        var chat = await CreateChatAsync();
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory);

        await AddSubAgentAsync(chat, "a1", "Sub Agent");

        var root = Assert.Single(viewModel.EditorItems);
        var subAgentsNode = root.Children.Single(c => c.Id == "chat-sub-agents");
        var subAgentNavItem = subAgentsNode.Children.Single(c => c.Id == "sub-agent-a1");
        var subChatDetails = subAgentNavItem.Children.Single(c => c.Id == "chat-details");

        // Completing hides the node from the (HideCompletedAgents) tree, but its cached document
        // must survive so re-selecting it never renders blank.
        ((AgentChat)chat.SubAgents.Single(s => s.AgentId == "a1"))
            .SetCompletionState(AgentChatCompletionState.Succeeded);

        viewModel.SelectedEditorItem = subChatDetails;

        Assert.NotNull(viewModel.SelectedDetailDocument);
        Assert.Same(subChatDetails.DetailContent, viewModel.SelectedDetailDocument!.DetailContent);
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
