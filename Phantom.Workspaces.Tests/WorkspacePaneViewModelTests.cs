using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacePaneViewModelTests
{
    [AvaloniaFact]
    public void HasNoRegions_IsTrueWhenEmpty_AndFalseWhenRegionsExist()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());

        Assert.True(pane.HasNoRegions);
        Assert.False(pane.HasRegions);

        pane.SetRegions(
        [
            new WorkspaceRegionViewModel
            {
                Id = "editor-center",
                Title = "Center",
                DockRegion = "center",
                RelativeSize = 1,
            },
        ]);

        Assert.False(pane.HasNoRegions);
        Assert.True(pane.HasRegions);
    }

    [AvaloniaFact]
    public void CloseTabCommand_RemovesTab_AndSelectsNeighbor()
    {
        var firstTab = new EntityWorkspaceTabViewModel
        {
            Id = "first",
            Title = "First",
            Entity = CreateWorkspaceEntity(),
        };
        var secondTab = new EntityWorkspaceTabViewModel
        {
            Id = "second",
            Title = "Second",
            Entity = CreateWorkspaceEntity(),
        };
        var thirdTab = new EntityWorkspaceTabViewModel
        {
            Id = "third",
            Title = "Third",
            Entity = CreateWorkspaceEntity(),
        };
        var region = new WorkspaceRegionViewModel
        {
            Id = "center",
            Title = "Center",
            DockRegion = "center",
            RelativeSize = 1,
        };
        region.Tabs.Add(firstTab);
        region.Tabs.Add(secondTab);
        region.Tabs.Add(thirdTab);
        region.SelectedTab = secondTab;

        region.CloseTabCommand.Execute(secondTab);

        Assert.Equal(2, region.Tabs.Count);
        Assert.DoesNotContain(secondTab, region.Tabs);
        Assert.Same(thirdTab, region.SelectedTab);
    }

    [AvaloniaFact]
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
    public void AnyTabIsRunning_TrueWhenPaneStatusIsRunning()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        pane.PaneStatus.RunningStatus = RunningStatus.Running;
        Assert.True(pane.AnyTabIsRunning);
    }

    [Fact]
    public void AnyTabIsRunning_FalseWhenPaneStatusBecomesIdle()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        pane.PaneStatus.RunningStatus = RunningStatus.Running;
        pane.PaneStatus.RunningStatus = RunningStatus.Idle;
        Assert.False(pane.AnyTabIsRunning);
    }

    [Fact]
    public void AnyTabIsRunning_RaisesPropertyChanged_WhenPaneStatusRunningChanges()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var raised = false;
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pane.AnyTabIsRunning))
                raised = true;
        };

        pane.PaneStatus.RunningStatus = RunningStatus.Running;

        Assert.True(raised);
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

        pane.PaneStatus.RunningStatus = RunningStatus.Running;

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
}
