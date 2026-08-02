using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using global::Dock.Model.Mvvm.Controls;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

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

    // ── WorkspaceDataTemplates — top-level DataTemplate presence ─────────────

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspaceDataTemplates_HasTopLevelDataTemplateFor_NotificationIndicatorTabHeaderItemViewModel()
    {
        var templates = new WorkspaceDataTemplates();
        var viewModel = new NotificationIndicatorTabHeaderItemViewModel();

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspaceDataTemplates_HasTopLevelDataTemplateFor_IconTabHeaderItemViewModel()
    {
        var templates = new WorkspaceDataTemplates();
        var viewModel = new IconTabHeaderItemViewModel { Icon = "🧠" };

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    // ── DockDataTemplates — DataTemplate presence for Dock header scope (#775) ─

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_HasDataTemplateFor_TabHeaderViewModel()
    {
        var templates = new DockDataTemplates();
        var viewModel = new TabHeaderViewModel { Title = "T" };

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    // ── #1181: TabHeader title TextBlock exposes full title via ToolTip.Tip ──

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderDataTemplate_TitleTextBlock_HasToolTipBoundToTitle()
    {
        var viewModel = new TabHeaderViewModel { Title = "My Tab" };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel);

        Assert.Equal("My Tab", ToolTip.GetTip(titleTextBlock));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderDataTemplate_LongTitle_ToolTipExposesFullUntruncatedText()
    {
        const string longTitle = "Copilot SDK sub-agent design document";
        var viewModel = new TabHeaderViewModel { Title = longTitle };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel);

        Assert.Equal(longTitle, ToolTip.GetTip(titleTextBlock));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderDataTemplate_TitleChanged_ToolTipUpdatesLive()
    {
        var viewModel = new TabHeaderViewModel { Title = "Original" };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel);

        viewModel.Title = "Renamed";

        Assert.Equal("Renamed", ToolTip.GetTip(titleTextBlock));
    }

    private static TextBlock InflateTabHeaderTitleTextBlock(TabHeaderViewModel viewModel)
    {
        var templates = new DockDataTemplates();
        var template = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));
        var control = template.Build(viewModel);
        Assert.NotNull(control);
        control!.DataContext = viewModel;

        var host = new ContentControl { Content = control };
        host.Measure(new Avalonia.Size(1000, 600));
        host.Arrange(new Avalonia.Rect(0, 0, 1000, 600));

        return control.GetLogicalDescendants()
            .OfType<TextBlock>()
            .First(tb => tb.Text == viewModel.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_HasDataTemplateFor_AgentRunningIndicatorTabHeaderItemViewModel()
    {
        var templates = new DockDataTemplates();
        var viewModel = new AgentRunningIndicatorTabHeaderItemViewModel();

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_HasDataTemplateFor_NotificationIndicatorTabHeaderItemViewModel()
    {
        var templates = new DockDataTemplates();
        var viewModel = new NotificationIndicatorTabHeaderItemViewModel();

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    // ── #1119: DockDataTemplates must include a template for WorkspacesPaneDock ─────

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_HasDataTemplateFor_WorkspacesPaneDock()
    {
        var templates = new DockDataTemplates();
        var viewModel = new WorkspacesPaneDock();

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    // ── AgentRunningIndicatorTabHeaderItemViewModel DataTemplate class ────────

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentRunningIndicatorDataTemplate_ProgressBar_UsesGlyphIndicatorClasses()
    {
        var viewModel = new AgentRunningIndicatorTabHeaderItemViewModel();
        var templates = new WorkspaceDataTemplates();
        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        var control = matchingTemplate.Build(viewModel);

        var progressBar = Assert.IsType<ProgressBar>(control);
        Assert.Contains("glyph-indicator", progressBar.Classes);
        Assert.Contains("pulsating-brain", progressBar.Classes);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NotificationIndicatorDataTemplate_ProgressBar_UsesGlyphIndicatorClasses()
    {
        var viewModel = new NotificationIndicatorTabHeaderItemViewModel();
        var templates = new WorkspaceDataTemplates();
        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        var control = matchingTemplate.Build(viewModel);

        var progressBar = Assert.IsType<ProgressBar>(control);
        Assert.Contains("glyph-indicator", progressBar.Classes);
        Assert.Contains("exclamation-indicator", progressBar.Classes);
    }

    // ── AgentSessionWorkspaceTabViewModel – SetReady wires tab header indicators

    [Fact]
    public async Task AgentSessionTab_SetReady_SetsTabHeader()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        Assert.NotNull(tab.TabHeader);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_TabHeaderContainsAgentRunningIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        var indicator = tab.TabHeader!.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().FirstOrDefault();
        Assert.NotNull(indicator);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_TabHeaderContainsNotificationIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        var indicator = tab.TabHeader!.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().FirstOrDefault();
        Assert.NotNull(indicator);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_RunningIndicator_InitiallyNotRunning()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        var indicator = tab.TabHeader!.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().Single();
        Assert.False(indicator.IsRunning);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_EffectiveTabHeaderContainsRunningAndNotificationIndicators()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        Assert.Contains(doc.EffectiveTabHeader.Items, i => i is AgentRunningIndicatorTabHeaderItemViewModel);
        Assert.Contains(doc.EffectiveTabHeader.Items, i => i is NotificationIndicatorTabHeaderItemViewModel);
    }

    // ── WorkspaceDocument – NotificationIndicatorTabHeaderItemViewModel.HasUnread tracks HasUnreadNotification

    [Fact]
    public async Task WorkspaceDocument_HasUnreadNotification_SetToTrue_SetsHasUnreadOnNotificationIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);
        tab.SetReady(agentViewModel, loggerFactory);

        doc.HasUnreadNotification = true;

        var indicator = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();
        Assert.True(indicator.HasUnread);
    }

    [Fact]
    public async Task WorkspaceDocument_HasUnreadNotification_SetToFalse_ClearsHasUnreadOnNotificationIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);
        tab.SetReady(agentViewModel, loggerFactory);

        doc.HasUnreadNotification = true;
        doc.HasUnreadNotification = false;

        var indicator = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();
        Assert.False(indicator.HasUnread);
    }

    private static async Task<AgentChat> CreateMinimalEchoAgentChatAsync()
    {
        const string EchoAgentDefinitionJson =
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """;
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
        });
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceDocument_EffectiveTabHeader_TitleIsNonEmpty_AfterRestore()
    {
        // Regression for #1190: after a full save-close-reopen cycle, every restored
        // WorkspaceDocument's EffectiveTabHeader.Title (bound in DockDataTemplates.axaml:136
        // via <TextBlock Text="{Binding Title}"/>) must be non-empty. Directly targets the
        // symptom the user reported.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var entityId = new Phantom.Workspaces.Data.EntityId("11901190-2222-4000-8000-000000000001");
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, entityId, """
            {
              "entity-id": "11901190-2222-4000-8000-000000000001",
              "entity-types": ["entity", "note"],
              "names": [["notes", "1190-header-tab"]],
              "display-name": { "default": "" },
              "content": { "mime-type": "text/markdown", "content": { "text": "h" } }
            }
            """);

        var workspaceId = new Phantom.Workspaces.Data.EntityId("11901190-2222-4000-8000-0000000000f1");
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Header Cycle WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), System.StringComparison.Ordinal));
        Assert.NotNull(pane);
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane!);

        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "1190-header-tab",
            Title = "before",
            Entity = entity,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(tab);

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "1190-header-tab");

        tab.Title = "After Modify";

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), System.StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(restoredPane!);

        var restoredDoc = MainWindowViewModel.EnumerateAllDocuments(restoredPane!.ContentLayout!)
            .First(d => d.Id == "1190-header-tab");
        Assert.False(string.IsNullOrEmpty(restoredDoc.EffectiveTabHeader.Title),
            $"EffectiveTabHeader.Title must be non-empty after restore (was '{restoredDoc.EffectiveTabHeader.Title}').");
        Assert.Equal("After Modify", restoredDoc.EffectiveTabHeader.Title);
    }
}
