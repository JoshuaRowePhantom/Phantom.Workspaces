using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class WebViewModelTests
{
    // --- TabHeader / FaviconTabHeaderItemViewModel ---

    [Fact]
    public void WebViewModel_Constructor_TabHeader_ContainsFaviconItem()
    {
        var vm = new WebViewModel("https://example.com") { Id = "web-favicon-1", Title = "T" };
        Assert.NotNull(vm.TabHeader);
        var favicon = vm.TabHeader!.Items.OfType<FaviconTabHeaderItemViewModel>().FirstOrDefault();
        Assert.NotNull(favicon);
    }

    [Fact]
    public void WebViewModel_Constructor_FaviconItem_UriIsNull()
    {
        var vm = new WebViewModel("https://example.com") { Id = "web-favicon-2", Title = "T" };
        var favicon = vm.TabHeader!.Items.OfType<FaviconTabHeaderItemViewModel>().Single();
        Assert.Null(favicon.FaviconUri);
    }

    [Fact]
    public void WebViewModel_SetFaviconUri_UpdatesFaviconItem()
    {
        var vm = new WebViewModel("https://example.com") { Id = "web-favicon-3", Title = "T" };
        vm.SetFaviconUri("https://example.com/favicon.ico");
        var favicon = vm.TabHeader!.Items.OfType<FaviconTabHeaderItemViewModel>().Single();
        Assert.Equal("https://example.com/favicon.ico", favicon.FaviconUri);
    }

    [Fact]
    public void WebViewModel_SetFaviconUri_ToNull_ClearsFaviconItem()
    {
        var vm = new WebViewModel("https://example.com") { Id = "web-favicon-4", Title = "T" };
        vm.SetFaviconUri("https://example.com/favicon.ico");
        vm.SetFaviconUri(null);
        var favicon = vm.TabHeader!.Items.OfType<FaviconTabHeaderItemViewModel>().Single();
        Assert.Null(favicon.FaviconUri);
    }

    // --- HomeUrl / HasHomeUrl / NavigateHomeCommand ---

    [Fact]
    public void Constructor_WithInitialUrl_ExposesHomeUrl()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-1", Title = "T" };
        Assert.Equal("https://example.com", vm.HomeUrl);
    }

    [Fact]
    public void Constructor_WithInitialUrl_HasHomeUrlIsTrue()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-2", Title = "T" };
        Assert.True(vm.HasHomeUrl);
    }

    [Fact]
    public void Constructor_WithEmptyInitialUrl_HasHomeUrlIsFalse()
    {
        var vm = new WebViewModel(string.Empty) { Id = "home-3", Title = "T" };
        Assert.False(vm.HasHomeUrl);
    }

    [Fact]
    public void NavigateHomeCommand_SetsSourceUriToHomeUrl()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-4", Title = "T" };
        vm.AddressBarUrl = "https://other.com";
        vm.SourceUri = new Uri("https://other.com");

        vm.NavigateHomeCommand.Execute(null);

        Assert.Equal(new Uri("https://example.com"), vm.SourceUri);
    }

    [Fact]
    public void NavigateHomeCommand_ResetsAddressBarUrl()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-5", Title = "T" };
        vm.AddressBarUrl = "https://other.com";

        vm.NavigateHomeCommand.Execute(null);

        Assert.Equal("https://example.com", vm.AddressBarUrl);
    }

    [Fact]
    public void UpdateCurrentUrl_UpdatesSourceUriBackingFieldWithoutRaisingSourceUriChanged()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-6", Title = "T" };
        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(WebViewModel.SourceUri);

        vm.UpdateCurrentUrl("https://example.com/page1");

        Assert.Equal(new Uri("https://example.com/page1"), vm.SourceUri);
        Assert.False(raised);
    }

    [Fact]
    public void NavigateHomeCommand_AfterBrowserNavigation_RaisesSourceUriChanged()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-7", Title = "T" };
        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(WebViewModel.SourceUri);
        vm.UpdateCurrentUrl("https://example.com/page1");

        vm.NavigateHomeCommand.Execute(null);

        Assert.True(raised);
        Assert.Equal(new Uri("https://example.com"), vm.SourceUri);
    }

    [Fact]
    public void ConfiguredWebView_TryGetUrlFromObjects_ReadsNavigationStartUrl()
    {
        var url = ConfiguredWebView.TryGetUrlFromObjects(new NavigationEventArgsStub { Uri = new Uri("https://example.com/page1") });

        Assert.Equal("https://example.com/page1", url);
    }

    [Fact]
    public void HomeUrlTooltip_WithHomeUrl_ReturnsGoToHomePageFollowedByUrl()
    {
        var vm = new WebViewModel("https://example.com") { Id = "home-tooltip-1", Title = "T" };
        Assert.Equal("Go to home page\nhttps://example.com", vm.HomeUrlTooltip);
    }

    [Fact]
    public void HomeUrlTooltip_WithoutHomeUrl_ReturnsGoToHomePage()
    {
        var vm = new WebViewModel(string.Empty) { Id = "home-tooltip-2", Title = "T" };
        Assert.Equal("Go to home page", vm.HomeUrlTooltip);
    }

    // --- SetPageTitle / titleFixed ---

    [Fact]
    public void SetPageTitle_WhenTitleNotFixed_UpdatesTitle()
    {
        var vm = new WebViewModel("https://example.com", tabService: null, titleFixed: false)
        {
            Id = "test-tab-1",
            Title = "Initial",
        };

        vm.SetPageTitle("New Title");

        Assert.Equal("New Title", vm.Title);
    }

    [Fact]
    public void SetPageTitle_WhenTitleFixed_DoesNotUpdateTitle()
    {
        var vm = new WebViewModel("https://example.com", tabService: null, titleFixed: true)
        {
            Id = "test-tab-2",
            Title = "Pinned",
        };

        vm.SetPageTitle("Page Title From Browser");

        Assert.Equal("Pinned", vm.Title);
    }

    [Fact]
    public void WebViewModel_ExplicitTitleSet_StopsFollowingPageTitle()
    {
        var vm = new WebViewModel("https://example.com", tabService: null, titleFixed: false)
        {
            Id = "test-tab-explicit",
            Title = "Initial",
        };

        vm.SetTitleExplicit("Pinned");
        vm.SetPageTitle("Page Title From Browser");

        Assert.True(vm.IsTitleExplicit);
        Assert.Equal("Pinned", vm.Title);
    }

    [Fact]
    public void SetPageTitle_WhenTitleFixed_TooltipStillReflectsPageTitle()
    {
        var vm = new WebViewModel("https://example.com", tabService: null, titleFixed: true)
        {
            Id = "test-tab-3",
            Title = "Pinned",
        };

        vm.SetPageTitle("Real Page Title");

        Assert.Contains("Real Page Title", vm.TabTooltip);
        Assert.Contains("https://example.com", vm.TabTooltip);
    }

    [Fact]
    public void SetPageTitle_WithLongTitle_RetainsFullTitleAndTooltip()
    {
        const string longTitle = "Consolidate duplicated JSON serializer options + default config-path logic (AllowedSecretsStore vs ConfigurationPersistenceService)";
        var vm = new WebViewModel("https://example.com", tabService: null, titleFixed: false)
        {
            Id = "test-tab-long-title",
            Title = "Initial",
        };

        vm.SetPageTitle(longTitle);

        Assert.Equal(longTitle, vm.Title);
        Assert.Contains(longTitle, vm.TabTooltip);
        Assert.Contains("https://example.com", vm.TabTooltip);
    }

    // --- OpenExternalEntityShortcutHandler: default key → display name, title not fixed ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWebTab_DefaultKey_TitleIsDisplayName_AndBrowserCanOverride()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var handler = new OpenExternalEntityShortcutHandler();
        var entity = CreateExternalEntity(
            "bb000001-0000-4000-8000-000000000001",
            "My Entity",
            new() { ["default"] = "https://example.com" });

        var handled = await handler.Handle(viewModel, Shortcut.Open, entity);

        Assert.True(handled);
        var webTab = Assert.IsType<WebViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        Assert.Equal("My Entity", webTab.Title);

        // titleFixed = false: page title from browser should update Title
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("Browser Page Title", webTab.Title);
    }

    // --- OpenExternalEntityShortcutHandler: named key → key name as title, title fixed ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWebTab_NamedKey_TitleIsKeyName_AndBrowserCannotOverride()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var handler = new OpenExternalEntityShortcutHandler();
        var entity = CreateExternalEntity(
            "bb000002-0000-4000-8000-000000000002",
            "My Entity",
            new() { ["Board"] = "https://example.com/board" });

        var handled = await handler.Handle(viewModel, Shortcut.Open, entity);

        Assert.True(handled);
        var webTab2 = Assert.IsType<WebViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        Assert.Equal("Board", webTab2.Title);

        // titleFixed = true: page title from browser must NOT update Title
        webTab2.SetPageTitle("Browser Page Title");
        Assert.Equal("Board", webTab2.Title);
    }

    // --- Workspace restore: explicit title from JSON wins and is pinned ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_ExplicitTitle_IsUsedAndPinned()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var externalEntityId = new EntityId("cc000001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "cc000001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "external"],
              "names": [["tests", "external", "pinned-title"]],
              "display-name": { "default": "External Thing" },
              "urls": { "default": "https://example.com" }
            }
            """);

        var workspaceEntityId = new EntityId("cc000002-0000-4000-8000-000000000002");
        var workspaceJson = $$"""
            {
              "entity-id": "cc000002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Test" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "ext-tab-explicit",
                      "title": "My Pinned Title",
                      "dock": "full",
                      "content": {
                        "target-entity-name": "{{externalEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var workspacePane = await InvokeCreateWorkspacePaneAsync(viewModel, workspaceEntityId, workspaceJson);
        var webTab = Assert.IsType<WebViewModel>(Assert.Single(workspacePane.Tabs));

        Assert.Equal("My Pinned Title", webTab.Title);

        // titleFixed = true because explicit title was provided
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("My Pinned Title", webTab.Title);
    }

    // --- Workspace restore: no explicit title + default key → display name, not pinned ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_NoExplicitTitle_DefaultKey_TitleIsDisplayName_NotFixed()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var externalEntityId = new EntityId("cc000003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "cc000003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "external"],
              "names": [["tests", "external", "default-key"]],
              "display-name": { "default": "External Thing" },
              "urls": { "default": "https://example.com" }
            }
            """);

        var workspaceEntityId = new EntityId("cc000004-0000-4000-8000-000000000004");
        var workspaceJson = $$"""
            {
              "entity-id": "cc000004-0000-4000-8000-000000000004",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Test Default Key" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "ext-tab-default",
                      "dock": "full",
                      "content": {
                        "target-entity-name": "{{externalEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var workspacePane = await InvokeCreateWorkspacePaneAsync(viewModel, workspaceEntityId, workspaceJson);
        var webTab2 = Assert.IsType<WebViewModel>(Assert.Single(workspacePane.Tabs));

        Assert.Equal("External Thing", webTab2.Title);

        // titleFixed = false: browser can update the title
        webTab2.SetPageTitle("Browser Page Title");
        Assert.Equal("Browser Page Title", webTab2.Title);
    }

    // --- Workspace restore: no explicit title + named key → key name, pinned ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_NoExplicitTitle_NamedKey_TitleIsKeyName_Fixed()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var externalEntityId = new EntityId("cc000005-0000-4000-8000-000000000005");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "cc000005-0000-4000-8000-000000000005",
              "entity-types": ["entity", "external"],
              "names": [["tests", "external", "named-key"]],
              "display-name": { "default": "External Thing" },
              "urls": { "Repos": "https://example.com/repos" }
            }
            """);

        var workspaceEntityId = new EntityId("cc000006-0000-4000-8000-000000000006");
        var workspaceJson = $$"""
            {
              "entity-id": "cc000006-0000-4000-8000-000000000006",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Test Named Key" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "ext-tab-named",
                      "dock": "full",
                      "content": {
                        "target-entity-name": "{{externalEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var workspacePane = await InvokeCreateWorkspacePaneAsync(viewModel, workspaceEntityId, workspaceJson);
        var webTab3 = Assert.IsType<WebViewModel>(Assert.Single(workspacePane.Tabs));

        Assert.Equal("Repos", webTab3.Title);

        // titleFixed = true: browser cannot override
        webTab3.SetPageTitle("Browser Page Title");
        Assert.Equal("Repos", webTab3.Title);
    }

    // --- DuplicateBrowserTabCommand ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithWebViewModel_OpensNewTabAtSameUrl()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com", viewModel) { Id = "web-dup-1", Title = "Tab" };
        await viewModel.OpenTabAsync(tab);

        await viewModel.DuplicateBrowserTabAsync();

        var tabs = viewModel.SelectedWorkspacePane.Tabs.ToList();
        var duplicate = tabs
            .OfType<WebViewModel>()
            .FirstOrDefault(t => t.Id != "web-dup-1" && t.AddressBarUrl == "https://example.com");

        Assert.NotNull(duplicate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithWebViewModel_InsertsNewTabAfterSource()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com", viewModel) { Id = "web-dup-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com", viewModel) { Id = "web-dup-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // Activate tab A so it becomes the active tab.
        await viewModel.OpenTabAsync(tabA);

        await viewModel.DuplicateBrowserTabAsync();

        // Fix #1065: WorkspacePaneViewModel.Tabs is an order-independent membership
        // set (#1107); assert visual ordering against the DocumentDock.VisibleDockables.
        var documentDock = FindDocumentDockInLayout(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(documentDock);
        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        var indexA = docs.FindIndex(d => d.Id == "web-dup-a");
        var indexDup = docs.FindIndex(d => d.TabViewModel is WebViewModel wv
            && wv.Id != "web-dup-a"
            && wv.Id != "web-dup-b"
            && wv.AddressBarUrl == "https://a.example.com");

        Assert.True(indexA >= 0, "Source tab A should be present");
        Assert.True(indexDup >= 0, "Duplicate tab should be present");
        Assert.Equal(indexA + 1, indexDup);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithNonBrowserTab_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Open an entity tab (non-browser tab).
        var entityTab = new EntityWorkspaceTabViewModel { Id = "entity-1", Title = "Entity" };
        await viewModel.OpenTabAsync(entityTab);

        var tabCountBeforeDuplicate = viewModel.SelectedWorkspacePane.Tabs.Count;

        await viewModel.DuplicateBrowserTabAsync();

        var tabs3 = viewModel.SelectedWorkspacePane.Tabs.ToList();
        // DuplicateBrowserTabAsync is a no-op for non-browser tabs; no new tab should have been inserted.
        Assert.Equal(tabCountBeforeDuplicate, tabs3.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithNoActiveTab_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Do not open any tabs.
        await viewModel.DuplicateBrowserTabAsync();

        // No exception; workspace pane has no tabs.
        Assert.Empty(viewModel.SelectedWorkspacePane.Tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithEmptyUrl_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("", viewModel) { Id = "web-empty-url", Title = "Tab" };
        await viewModel.OpenTabAsync(tab);

        await viewModel.DuplicateBrowserTabAsync();

        Assert.Single(viewModel.SelectedWorkspacePane.Tabs);
    }

    // --- RaiseOpenNewWindow: new tab insertion position ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RaiseOpenNewWindow_InsertsNewTabImmediatelyRightOfSourceTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Open two tabs so we can verify relative insertion position.
        var tabA = new WebViewModel("https://a.example.com", viewModel) { Id = "web-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com", viewModel) { Id = "web-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // Simulate a new-window navigation originating from tab A (Id = "web-a").
        var sourceTab = new WebViewModel("https://a.example.com", viewModel) { Id = "web-a", Title = "A" };
        sourceTab.RaiseOpenNewWindow("https://new.example.com");

        // Allow the async void to complete.
        await Task.Yield();

        // Fix #1065: assert visual ordering against the DocumentDock.VisibleDockables.
        var documentDock = FindDocumentDockInLayout(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(documentDock);
        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();

        var indexA = docs.FindIndex(d => d.Id == "web-a");
        var indexNew = docs.FindIndex(d => d.TabViewModel is WebViewModel wv
            && wv.AddressBarUrl == "https://new.example.com");

        Assert.True(indexA >= 0, "Source tab A should be present");
        Assert.True(indexNew >= 0, "New tab should be present");
        Assert.Equal(indexA + 1, indexNew);
    }

    private static IDocumentDock? FindDocumentDockInLayout(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }
        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockInLayout(child);
                if (result is not null) return result;
            }
        }
        return null;
    }

    // --- #1325: restored browser tabs have their tabService wired ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_RestoredBrowserTab_HasTabServiceWired()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var restored = await InvokeCreateTabFromBrowserDescriptorAsync(
            viewModel, "https://restored.example.com", "restored-web-1");

        var webVm = Assert.IsType<WebViewModel>(restored);
        Assert.Same(viewModel, GetTabService(webVm));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_RestoredBrowserTab_RaiseOpenNewWindow_InsertsNewTabInDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var restored = await InvokeCreateTabFromBrowserDescriptorAsync(
            viewModel, "https://restored.example.com", "restored-web-2");
        var webVm = Assert.IsType<WebViewModel>(restored);
        await viewModel.OpenTabAsync(webVm);

        // A restored tab must re-connect NewWindowRequested → OpenTabAsync.
        webVm.RaiseOpenNewWindow("https://opened-from-restored.example.com");
        await Task.Yield();

        var documentDock = FindDocumentDockInLayout(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(documentDock);
        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        var indexSource = docs.FindIndex(d => d.Id == "restored-web-2");
        var indexNew = docs.FindIndex(d => d.TabViewModel is WebViewModel wv
            && wv.AddressBarUrl == "https://opened-from-restored.example.com");

        Assert.True(indexSource >= 0, "Restored source tab should be present");
        Assert.True(indexNew >= 0, "New tab should be present");
        Assert.Equal(indexSource + 1, indexNew);
    }

    // --- #1333: restored web tab anchors new-window opens on its own id ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WebViewModel_RaiseOpenNewWindow_WithTabServiceFromRestore_CallsOpenTabAsyncWithSourceId()
    {
        var tabService = new RecordingTabService();
        var restoredWebVm = new WebViewModel("https://restored.example.com", tabService)
        {
            Id = "restored-anchor-1",
            Title = "Restored",
        };

        restoredWebVm.RaiseOpenNewWindow("https://mr9-new.example.com");
        await tabService.OpenTabInvoked.Task;

        Assert.Equal("restored-anchor-1", tabService.LastInsertAfterTabId);
    }

    private sealed class RecordingTabService : IWorkspaceTabService
    {
        public TaskCompletionSource OpenTabInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastInsertAfterTabId { get; private set; }

        public Task OpenTabAsync(WorkspaceTabViewModel tab, string? insertAfterTabId = null, bool focus = true, string? workspacePaneId = null)
        {
            this.LastInsertAfterTabId = insertAfterTabId;
            this.OpenTabInvoked.TrySetResult();
            return Task.CompletedTask;
        }

        public Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab) => Task.CompletedTask;

        public void CloseTab(WorkspaceTabViewModel tab)
        {
        }

        public Task<bool> TryFocusExistingWebTabAsync(string url) => Task.FromResult(false);
    }

    [Fact]
    public void ConfiguredWebView_OnNewWindowRequested_WhenHandlerRuns_SetsArgsHandledTrue()
    {
        var vm = new WebViewModel("https://source.example.com", tabService: null)
        {
            Id = "web-handled-1",
            Title = "Source",
        };
        var args = new FakeNewWindowArgs { Request = new Uri("https://popup.example.com"), Handled = false };

        ConfiguredWebView.HandleNewWindowRequested(args, vm);

        Assert.True(args.Handled);
    }

    [Fact]
    public void ConfiguredWebView_OnNewWindowRequested_NullViewModel_DoesNotSetHandled()
    {
        var args = new FakeNewWindowArgs { Request = new Uri("https://popup.example.com"), Handled = false };

        ConfiguredWebView.HandleNewWindowRequested(args, null);

        Assert.False(args.Handled);
    }

    private sealed class FakeNewWindowArgs
    {
        public Uri? Request { get; set; }
        public bool Handled { get; set; }
    }

    private static IWorkspaceTabService? GetTabService(WebViewModel webVm)
    {
        var field = typeof(WebViewModel).GetField(
            "tabService",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (IWorkspaceTabService?)field!.GetValue(webVm);
    }

    private static async Task<WorkspaceTabViewModel?> InvokeCreateTabFromBrowserDescriptorAsync(
        MainWindowViewModel viewModel,
        string url,
        string tabId)
    {
        var workspaceEntity = CreateExternalEntity(
            Guid.NewGuid().ToString(),
            "workspace",
            new System.Collections.Generic.Dictionary<string, string>());
        var descriptor = new BrowserDockTabDescriptor(url);

        var method = typeof(MainWindowViewModel).GetMethod(
            "CreateTabViewModelFromDescriptorAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<WorkspaceTabViewModel?>?)method!.Invoke(
            viewModel,
            [workspaceEntity, descriptor, tabId]);
        Assert.NotNull(task);
        return await task!;
    }

    // --- FocusUrlBarCommand ---

    [Fact]
    public void FocusUrlBarCommand_IsAlwaysExecutable()
    {
        var vm = new WebViewModel("https://example.com") { Id = "focus-1", Title = "T" };
        Assert.True(vm.FocusUrlBarCommand.CanExecute(null));
    }

    [Fact]
    public void FocusUrlBarCommand_Execute_RaisesFocusUrlBarRequested()
    {
        var vm = new WebViewModel("https://example.com") { Id = "focus-2", Title = "T" };
        var raised = false;
        vm.FocusUrlBarRequested += (_, _) => raised = true;

        vm.FocusUrlBarCommand.Execute(null);

        Assert.True(raised);
    }

    private static SubscribedEntityViewModel CreateExternalEntity(
        string entityId,
        string displayName,
        System.Collections.Generic.Dictionary<string, string> urls)
    {
        var urlsJson = JsonSerializer.Serialize(urls);
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "external"],
              "names": [["tests", "external", "{{entityId}}"]],
              "display-name": { "default": "{{displayName}}" },
              "urls": {{urlsJson}}
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(entityId),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            },
            deleteEntityAsync: null);
    }

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var prop = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        return Assert.IsType<EntityBroker>(prop!.GetValue(viewModel));
    }

    private static MainWindowViewModel CreateTestMainWindowViewModel()
        => new(new UnknownRepositorySource(), new WorkspacesConfiguration { SkipStartupWorkspace = true });

    private static async Task UpsertEntityAndLoadAsync(
        EntityBroker entityBroker,
        EntityId entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "Add test entity." },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    EntityChangeMode = EntityChangeMode.Replace,
                    Data = document.RootElement.Clone(),
                },
            ],
        });
        // Subscribe the entity in-memory so TryGetReferencedEntity can resolve it.
        await entityBroker.GetEntitiesAsync([entityId]);
    }

    private static async Task<WorkspacePaneViewModel> InvokeCreateWorkspacePaneAsync(
        MainWindowViewModel viewModel,
        EntityId workspaceEntityId,
        string workspaceJson)
    {
        using var workspaceDoc = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = workspaceEntityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = workspaceDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var method = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<WorkspacePaneViewModel>?)method!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDoc.RootElement.Clone()]);
        Assert.NotNull(task);

        return await task!;
    }

    // ── Accelerator-key event forwarding ──────────────────────────────────────────

    [Fact]
    public void WebViewModel_RaiseGoToTabAtIndex_FiresGoToTabAtIndexRequestedWithIndex()
    {
        var vm = new WebViewModel("https://example.com") { Id = "accel-goto-1", Title = "T" };
        int? received = null;
        vm.GoToTabAtIndexRequested += (_, idx) => received = idx;

        vm.RaiseGoToTabAtIndex(3);

        Assert.Equal(3, received);
    }

    [Fact]
    public void WebViewModel_RaiseAltKeyStateChanged_True_FiresWithTrue()
    {
        var vm = new WebViewModel("https://example.com") { Id = "accel-alt-true", Title = "T" };
        bool? received = null;
        vm.AltKeyStateChanged += (_, v) => received = v;

        vm.RaiseAltKeyStateChanged(true);

        Assert.True(received);
    }

    [Fact]
    public void WebViewModel_RaiseAltKeyStateChanged_False_FiresWithFalse()
    {
        var vm = new WebViewModel("https://example.com") { Id = "accel-alt-false", Title = "T" };
        bool? received = null;
        vm.AltKeyStateChanged += (_, v) => received = v;

        vm.RaiseAltKeyStateChanged(false);

        Assert.False(received);
    }
    private sealed class NavigationEventArgsStub
    {
        public Uri? Uri { get; init; }
    }
}
