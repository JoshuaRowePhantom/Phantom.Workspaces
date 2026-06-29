using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class TabHeaderViewModelTests
{
    // ── FaviconTabHeaderItemViewModel ────────────────────────────────────────

    [Fact]
    public void FaviconTabHeaderItemViewModel_FaviconUri_DefaultIsNull()
    {
        var item = new FaviconTabHeaderItemViewModel();
        Assert.Null(item.FaviconUri);
    }

    [Fact]
    public void FaviconTabHeaderItemViewModel_SetFaviconUri_RaisesPropertyChanged()
    {
        var item = new FaviconTabHeaderItemViewModel();
        var raised = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.FaviconUri))
                raised = true;
        };

        item.FaviconUri = "https://example.com/favicon.ico";

        Assert.True(raised);
    }

    // ── AgentRunningIndicatorTabHeaderItemViewModel ──────────────────────────

    [Fact]
    public void AgentRunningIndicator_IsRunning_DefaultIsFalse()
    {
        var indicator = new AgentRunningIndicatorTabHeaderItemViewModel();
        Assert.False(indicator.IsRunning);
    }

    [Fact]
    public void AgentRunningIndicator_SetIsRunning_RaisesPropertyChanged()
    {
        var indicator = new AgentRunningIndicatorTabHeaderItemViewModel();
        var raised = false;
        indicator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(indicator.IsRunning))
                raised = true;
        };

        indicator.IsRunning = true;

        Assert.True(raised);
    }

    // ── AgentSessionWorkspaceTabViewModel – TabStatus tracks running state ────

    [Fact]
    public void AgentSessionTab_TabStatus_IsNotNull()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        Assert.NotNull(tab.TabStatus);
    }

    [Fact]
    public void AgentSessionTab_TabStatus_IsIdle_Initially()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        Assert.Equal(RunningStatus.Idle, tab.TabStatus!.RunningStatus);
    }

    [Fact]
    public void AgentSessionTab_TabHeader_IsNull_ByDefault()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        Assert.Null(tab.TabHeader);
    }

    [Fact]
    public void AgentSessionTab_EffectiveTabHeader_ContainsStatusIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(indicator);
    }

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

    // ── WorkspaceDocument – EffectiveTabHeader always contains the status indicator ─

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_ContainsStatusIndicatorItem()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t2", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(indicator);
    }

    [Fact]
    public void WorkspaceDocument_HasUnreadNotification_SetToTrue_SetsErrorStatusOnStatusIndicator()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t3", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .Single();
        Assert.Equal(ErrorStatus.Error, indicator.Status.ErrorStatus);
    }

    [Fact]
    public void WorkspaceDocument_HasUnreadNotification_SetToFalse_ClearsErrorStatusOnStatusIndicator()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t4", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;
        doc.HasUnreadNotification = false;

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .Single();
        Assert.Equal(ErrorStatus.None, indicator.Status.ErrorStatus);
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

    // ── AltShortcutLabel ─────────────────────────────────────────────────────

    [Fact]
    public void TabHeaderViewModel_AltShortcutLabel_DefaultIsNull()
    {
        var vm = new TabHeaderViewModel { Title = "T" };
        Assert.Null(vm.AltShortcutLabel);
    }

    [Fact]
    public void TabHeaderViewModel_AltShortcutLabel_SetValue_RaisesPropertyChanged()
    {
        var vm = new TabHeaderViewModel { Title = "T" };
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.AltShortcutLabel))
                raised = true;
        };

        vm.AltShortcutLabel = "1";

        Assert.True(raised);
    }

    // ── RefreshTabAltShortcutLabels ──────────────────────────────────────────

    private static IDocumentDock CreateDockWithDocs(int count)
    {
        var dock = new Dock.Model.Mvvm.Controls.DocumentDock();
        dock.VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<Dock.Model.Core.IDockable>();
        for (var i = 0; i < count; i++)
        {
            var tab = new EntityWorkspaceTabViewModel { Id = $"tab-{i}", Title = $"Tab {i}" };
            dock.VisibleDockables.Add(new WorkspaceDocument(tab));
        }
        return dock;
    }

    [Fact]
    public void RefreshTabAltShortcutLabels_FirstDoc_GetsLabel1()
    {
        var dock = CreateDockWithDocs(3);
        MainWindowViewModel.RefreshTabAltShortcutLabels(dock);

        var label = ((WorkspaceDocument)dock.VisibleDockables![0]).EffectiveTabHeader.AltShortcutLabel;
        Assert.Equal("1", label);
    }

    [Fact]
    public void RefreshTabAltShortcutLabels_TenthDoc_GetsLabel0()
    {
        var dock = CreateDockWithDocs(10);
        MainWindowViewModel.RefreshTabAltShortcutLabels(dock);

        var label = ((WorkspaceDocument)dock.VisibleDockables![9]).EffectiveTabHeader.AltShortcutLabel;
        Assert.Equal("0", label);
    }

    [Fact]
    public void RefreshTabAltShortcutLabels_EleventhDoc_GetsNullLabel()
    {
        var dock = CreateDockWithDocs(11);
        MainWindowViewModel.RefreshTabAltShortcutLabels(dock);

        var label = ((WorkspaceDocument)dock.VisibleDockables![10]).EffectiveTabHeader.AltShortcutLabel;
        Assert.Null(label);
    }

    // ── AgentRunningIndicatorTabHeaderItemViewModel DataTemplate class ────────

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentRunningIndicatorDataTemplate_TextBlock_UsesAgentTabHeaderBrainClass()
    {
        var viewModel = new AgentRunningIndicatorTabHeaderItemViewModel();
        var templates = new WorkspaceDataTemplates();
        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        var control = matchingTemplate.Build(viewModel);

        var textBlock = Assert.IsType<TextBlock>(control);
        Assert.Contains("agent-tab-header-brain", textBlock.Classes);
        Assert.DoesNotContain("agent-chat-status-line-brain", textBlock.Classes);
    }
}
