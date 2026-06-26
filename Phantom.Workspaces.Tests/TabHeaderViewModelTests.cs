using System.ComponentModel;
using System.Linq;
using Dock.Model.Core;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class TabHeaderViewModelTests
{
    // ── NotificationIndicatorTabHeaderItemViewModel ──────────────────────────

    [Fact]
    public void NotificationIndicatorTabHeaderItemViewModel_HasUnread_DefaultIsFalse()
    {
        var indicator = new NotificationIndicatorTabHeaderItemViewModel();
        Assert.False(indicator.HasUnread);
    }

    [Fact]
    public void NotificationIndicatorTabHeaderItemViewModel_SetHasUnread_RaisesPropertyChanged()
    {
        var indicator = new NotificationIndicatorTabHeaderItemViewModel();
        var raised = false;
        indicator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(indicator.HasUnread))
            {
                raised = true;
            }
        };

        indicator.HasUnread = true;

        Assert.True(raised);
    }

    // ── WorkspaceDocument – title does NOT get "! " prefix ───────────────────

    [Fact]
    public void WorkspaceDocument_Title_DoesNotPrependExclamationMark_WhenHasUnreadNotification()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t1", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;

        Assert.DoesNotContain("!", doc.Title);
    }

    // ── WorkspaceDocument – EffectiveTabHeader always contains the indicator ─

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_ContainsNotificationIndicatorItem()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t2", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<NotificationIndicatorTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(indicator);
    }

    [Fact]
    public void WorkspaceDocument_HasUnreadNotification_SetToTrue_SetsHasUnreadOnIndicator()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t3", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<NotificationIndicatorTabHeaderItemViewModel>()
            .Single();
        Assert.True(indicator.HasUnread);
    }

    [Fact]
    public void WorkspaceDocument_HasUnreadNotification_SetToFalse_ClearsHasUnreadOnIndicator()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t4", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;
        doc.HasUnreadNotification = false;

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<NotificationIndicatorTabHeaderItemViewModel>()
            .Single();
        Assert.False(indicator.HasUnread);
    }

    // ── WorkspaceDocument – icon items are preserved from TabHeader ──────────

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_WithIconTabHeader_ContainsIconItem()
    {
        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "t5",
            Title = "My Tab",
            TabHeader = TabHeaderViewModel.WithIcon("🧠", "My Tab"),
        };
        var doc = new WorkspaceDocument(tab);

        var iconItem = doc.EffectiveTabHeader.Items
            .OfType<IconTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(iconItem);
        Assert.Equal("🧠", iconItem!.Icon);
    }

    // ── WorkspaceDocument – EffectiveTabHeader.Title tracks tab title ────────

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_Title_MatchesTabTitle()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t6", Title = "Some Title" };
        var doc = new WorkspaceDocument(tab);

        Assert.Equal("Some Title", doc.EffectiveTabHeader.Title);
    }

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_Title_UpdatesWhenTabTitleChanges()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t7", Title = "Original" };
        var doc = new WorkspaceDocument(tab);

        tab.Title = "Updated";

        Assert.Equal("Updated", doc.EffectiveTabHeader.Title);
    }
}
