using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacePaneViewModelTests
{
    // ── HasNoTabs / HasTabs ───────────────────────────────────────────────────

    [Fact]
    public void HasNoTabs_IsTrueWhenEmpty_AndFalseWhenTabsExist()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());

        Assert.Empty(pane.Tabs);

        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "tab-1",
            Title = "Tab 1",
            Entity = CreateWorkspaceEntity(),
        };
        pane.Tabs.Add(tab);

        Assert.Single(pane.Tabs);
    }

    [PhantomAvaloniaFact]
    public async Task EntityWorkspaceTabViewModel_UsesEntityCardNodeWithDeleteCommand()
    {
        var deleteInvocations = 0;
        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "entity-tab",
            Title = "Entity Tab",
            Entity = CreateWorkspaceEntity(
                _ =>
                {
                    deleteInvocations++;
                    return Task.CompletedTask;
                }),
        };

        var cardNode = Assert.IsType<EntityListNodeViewModel>(tab.EntityCardNode);
        Assert.True(cardNode.Card.ShowDeleteButton);
        Assert.Equal(EntityCardViewResolver.RawViewName, cardNode.Card.CardViewName);
        cardNode.Card.DeleteEntityCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(1, deleteInvocations);
    }

    // ── AnyTabIsRunning ───────────────────────────────────────────────────────

    [Fact]
    public void AnyTabIsRunning_DefaultIsFalse()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        Assert.False(pane.AnyTabIsRunning);
    }

    [Fact]
    public void AnyTabIsRunning_TrueWhenTabWithRunningStatusAdded()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Running;
        var tab = new TestRunningTab("running-tab", tabStatus);

        pane.Tabs.Add(tab);

        Assert.True(pane.AnyTabIsRunning);
    }

    [Fact]
    public void AnyTabIsRunning_FalseWhenTabStatusBecomesIdle()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Running;
        var tab = new TestRunningTab("running-tab", tabStatus);
        pane.Tabs.Add(tab);

        tabStatus.RunningStatus = RunningStatus.Idle;

        Assert.False(pane.AnyTabIsRunning);
    }

    [Fact]
    public void AnyTabIsRunning_RaisesPropertyChanged_WhenTabStatusRunningChanges()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var tabStatus = new StatusItem();
        var tab = new TestRunningTab("running-tab", tabStatus);
        pane.Tabs.Add(tab);

        var raised = false;
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pane.AnyTabIsRunning))
                raised = true;
        };

        tabStatus.RunningStatus = RunningStatus.Running;

        Assert.True(raised);
    }

    [Fact]
    public void AnyTabIsRunning_FalseAfterRunningTabIsRemoved()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Running;
        var tab = new TestRunningTab("running-tab", tabStatus);
        pane.Tabs.Add(tab);

        pane.Tabs.Remove(tab);

        Assert.False(pane.AnyTabIsRunning);
    }

    // ── AnyTabHasUnreadNotification ───────────────────────────────────────────

    [Fact]
    public void AnyTabHasUnreadNotification_DefaultIsFalse()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        Assert.False(pane.AnyTabHasUnreadNotification);
    }

    [Fact]
    public void AnyTabHasUnreadNotification_SetToTrue_IsTrue()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        pane.AnyTabHasUnreadNotification = true;
        Assert.True(pane.AnyTabHasUnreadNotification);
    }

    [Fact]
    public void AnyTabHasUnreadNotification_SetToTrue_RaisesPropertyChanged()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var raised = false;
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pane.AnyTabHasUnreadNotification))
                raised = true;
        };

        pane.AnyTabHasUnreadNotification = true;

        Assert.True(raised);
    }

    // ── WorkspacePaneDocument – EffectiveTabHeader indicators ─────────────────

    [Fact]
    public void WorkspacePaneDocument_EffectiveTabHeader_ContainsRunningIndicator()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        var indicator = doc.EffectiveTabHeader.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().FirstOrDefault();

        Assert.NotNull(indicator);
    }

    [Fact]
    public void WorkspacePaneDocument_EffectiveTabHeader_ContainsNotificationIndicator()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        var indicator = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().FirstOrDefault();

        Assert.NotNull(indicator);
    }

    [Fact]
    public void WorkspacePaneDocument_AnyTabIsRunning_PropagatesTo_RunningIndicator()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Running;
        pane.Tabs.Add(new TestRunningTab("running-tab", tabStatus));

        var indicator = doc.EffectiveTabHeader.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().Single();
        Assert.True(indicator.IsRunning);
    }

    [Fact]
    public void WorkspacePaneDocument_AnyTabHasUnreadNotification_PropagatesTo_NotificationIndicator()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        pane.AnyTabHasUnreadNotification = true;

        var indicator = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();
        Assert.True(indicator.HasUnread);
    }

    [Fact]
    public void WorkspacePaneDocument_EffectiveTabHeader_Title_MatchesPaneTitle()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        Assert.Equal(pane.Title, doc.EffectiveTabHeader.Title);
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity(
        Func<SubscribedEntityViewModel, Task>? deleteEntityAsync = null)
    {
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Workspace" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("11111111-1111-1111-1111-111111111111"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            },
            deleteEntityAsync);
    }

    /// <summary>Test stub: a tab whose TabStatus is a settable StatusItem.</summary>
    private sealed class TestRunningTab : WorkspaceTabViewModel
    {
        private readonly StatusItem statusItem;

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public TestRunningTab(string id, StatusItem statusItem)
        {
            this.Id = id;
            this.Title = id;
            this.DockRegion = "full";
            this.statusItem = statusItem;
        }

        public override IStatusItem? TabStatus => this.statusItem;
    }
}
