using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;
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

    // ── IsShortcutBadgeVisible ───────────────────────────────────────────────

    [Fact]
    public void TabHeaderViewModel_IsShortcutBadgeVisible_DefaultFalse()
    {
        var vm = new TabHeaderViewModel { Title = "T" };
        Assert.False(vm.IsShortcutBadgeVisible);
    }

    [Fact]
    public void TabHeaderViewModel_IsShortcutBadgeVisible_RaisesPropertyChanged()
    {
        var vm = new TabHeaderViewModel { Title = "T" };
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsShortcutBadgeVisible))
                raised = true;
        };

        vm.IsShortcutBadgeVisible = true;

        Assert.True(raised);
    }

    [Fact]
    public void TabHeaderViewModel_DoesNotHaveIsAltHeldProperty()
    {
        var property = typeof(TabHeaderViewModel).GetProperty("IsAltHeld");
        Assert.Null(property);
    }

    // ── RefreshTabAltShortcutLabels ──────────────────────────────────────────

    private static (WorkspacePaneViewModel pane, System.Collections.Generic.Dictionary<string, WorkspaceDocument> docs)
        CreatePaneWithDocs(int count)
    {
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(
            """{"entity-id":"aaaaaaaa-0000-4000-8000-aaaaaaaaaaaa","entity-types":["entity","workspace"],"display-name":{"default":"Test"}}""");
        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-0000-4000-8000-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(System.DateTimeOffset.UtcNow, "1"),
                Data = jsonDoc.RootElement.Clone(),
                Relationships = System.Array.Empty<EntitySnapshot>(),
            });

        var pane = new WorkspacePaneViewModel(entity);
        var docs = new System.Collections.Generic.Dictionary<string, WorkspaceDocument>(System.StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var tab = new EntityWorkspaceTabViewModel { Id = $"tab-{i}", Title = $"Tab {i}" };
            pane.Tabs.Add(tab);
            docs[tab.Id] = new WorkspaceDocument(tab);
        }
        return (pane, docs);
    }

    [Fact]
    public void RefreshTabAltShortcutLabels_FirstDoc_GetsLabel1()
    {
        var (pane, docs) = CreatePaneWithDocs(3);
        MainWindowViewModel.RefreshTabAltShortcutLabels(pane, id => docs.TryGetValue(id, out var doc) ? doc : null);

        Assert.Equal("1", docs["tab-0"].EffectiveTabHeader.AltShortcutLabel);
    }

    [Fact]
    public void RefreshTabAltShortcutLabels_TenthDoc_GetsLabel0()
    {
        var (pane, docs) = CreatePaneWithDocs(10);
        MainWindowViewModel.RefreshTabAltShortcutLabels(pane, id => docs.TryGetValue(id, out var doc) ? doc : null);

        Assert.Equal("0", docs["tab-9"].EffectiveTabHeader.AltShortcutLabel);
    }

    [Fact]
    public void RefreshTabAltShortcutLabels_EleventhDoc_GetsNullLabel()
    {
        var (pane, docs) = CreatePaneWithDocs(11);
        MainWindowViewModel.RefreshTabAltShortcutLabels(pane, id => docs.TryGetValue(id, out var doc) ? doc : null);

        Assert.Null(docs["tab-10"].EffectiveTabHeader.AltShortcutLabel);
    }

    // ── WorkspaceDataTemplates — top-level DataTemplate presence ─────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void WorkspaceDataTemplates_HasTopLevelDataTemplateFor_NotificationIndicatorTabHeaderItemViewModel()
    {
        var templates = new WorkspaceDataTemplates();
        var viewModel = new NotificationIndicatorTabHeaderItemViewModel();

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void WorkspaceDataTemplates_HasTopLevelDataTemplateFor_IconTabHeaderItemViewModel()
    {
        var templates = new WorkspaceDataTemplates();
        var viewModel = new IconTabHeaderItemViewModel { Icon = "🧠" };

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    // ── AgentRunningIndicatorTabHeaderItemViewModel DataTemplate class ────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
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

    [PhantomAvaloniaFact(Timeout = 15_000)]
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
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);

        tab.SetReady(agentViewModel, loggerFactory);

        Assert.NotNull(tab.TabHeader);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_TabHeaderContainsAgentRunningIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);

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
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);
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
        await using var agentViewModel = new AgentViewModel(agentChat, "test", loggerFactory);
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
}
