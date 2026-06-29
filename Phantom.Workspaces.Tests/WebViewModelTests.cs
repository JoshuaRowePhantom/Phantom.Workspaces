using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

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

    // --- OpenExternalEntityShortcutHandler: default key → display name, title not fixed ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWebTab_DefaultKey_TitleIsDisplayName_AndBrowserCanOverride()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var handler = new OpenExternalEntityShortcutHandler();
        var entity = CreateExternalEntity(
            "bb000001-0000-4000-8000-000000000001",
            "My Entity",
            new() { ["default"] = "https://example.com" });

        var handled = await handler.Handle(viewModel, Shortcut.Open, entity);

        Assert.True(handled);
        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var webTab = Assert.IsType<WebViewModel>(selectedRegion.SelectedTab);
        Assert.Equal("My Entity", webTab.Title);

        // titleFixed = false: page title from browser should update Title
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("Browser Page Title", webTab.Title);
    }

    // --- OpenExternalEntityShortcutHandler: named key → key name as title, title fixed ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWebTab_NamedKey_TitleIsKeyName_AndBrowserCannotOverride()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var handler = new OpenExternalEntityShortcutHandler();
        var entity = CreateExternalEntity(
            "bb000002-0000-4000-8000-000000000002",
            "My Entity",
            new() { ["Board"] = "https://example.com/board" });

        var handled = await handler.Handle(viewModel, Shortcut.Open, entity);

        Assert.True(handled);
        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var webTab = Assert.IsType<WebViewModel>(selectedRegion.SelectedTab);
        Assert.Equal("Board", webTab.Title);

        // titleFixed = true: page title from browser must NOT update Title
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("Board", webTab.Title);
    }

    // --- Workspace restore: explicit title from JSON wins and is pinned ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_ExplicitTitle_IsUsedAndPinned()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        var tabs = workspacePane.SelectedRegion?.Tabs;
        Assert.NotNull(tabs);
        var webTab = Assert.IsType<WebViewModel>(Assert.Single(tabs!));

        Assert.Equal("My Pinned Title", webTab.Title);

        // titleFixed = true because explicit title was provided
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("My Pinned Title", webTab.Title);
    }

    // --- Workspace restore: no explicit title + default key → display name, not pinned ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_NoExplicitTitle_DefaultKey_TitleIsDisplayName_NotFixed()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        var tabs = workspacePane.SelectedRegion?.Tabs;
        Assert.NotNull(tabs);
        var webTab = Assert.IsType<WebViewModel>(Assert.Single(tabs!));

        Assert.Equal("External Thing", webTab.Title);

        // titleFixed = false: browser can update the title
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("Browser Page Title", webTab.Title);
    }

    // --- Workspace restore: no explicit title + named key → key name, pinned ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_NoExplicitTitle_NamedKey_TitleIsKeyName_Fixed()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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
        var tabs = workspacePane.SelectedRegion?.Tabs;
        Assert.NotNull(tabs);
        var webTab = Assert.IsType<WebViewModel>(Assert.Single(tabs!));

        Assert.Equal("Repos", webTab.Title);

        // titleFixed = true: browser cannot override
        webTab.SetPageTitle("Browser Page Title");
        Assert.Equal("Repos", webTab.Title);
    }

    // --- DuplicateBrowserTabCommand ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithWebViewModel_OpensNewTabAtSameUrl()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com", viewModel) { Id = "web-dup-1", Title = "Tab" };
        await viewModel.OpenTabAsync(tab);

        await viewModel.DuplicateBrowserTabAsync();

        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var tabs = selectedRegion.Tabs!.ToList();
        var duplicate = tabs
            .OfType<WebViewModel>()
            .FirstOrDefault(t => t.Id != "web-dup-1" && t.AddressBarUrl == "https://example.com");

        Assert.NotNull(duplicate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithWebViewModel_InsertsNewTabAfterSource()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com", viewModel) { Id = "web-dup-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com", viewModel) { Id = "web-dup-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // Activate tab A so it becomes the active tab.
        await viewModel.OpenTabAsync(tabA);

        await viewModel.DuplicateBrowserTabAsync();

        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var tabs = selectedRegion.Tabs!.ToList();
        var indexA = tabs.FindIndex(t => t.Id == "web-dup-a");
        var indexDup = tabs.FindIndex(t => t is WebViewModel wv && wv.Id != "web-dup-a" && wv.Id != "web-dup-b" && wv.AddressBarUrl == "https://a.example.com");

        Assert.True(indexA >= 0, "Source tab A should be present");
        Assert.True(indexDup >= 0, "Duplicate tab should be present");
        Assert.Equal(indexA + 1, indexDup);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithNonBrowserTab_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Open an entity tab (non-browser tab).
        var entityTab = new EntityWorkspaceTabViewModel { Id = "entity-1", Title = "Entity" };
        await viewModel.OpenTabAsync(entityTab);

        var tabCountBeforeDuplicate = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion).Tabs.Count;

        await viewModel.DuplicateBrowserTabAsync();

        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var tabs = selectedRegion.Tabs!.ToList();
        // DuplicateBrowserTabAsync is a no-op for non-browser tabs; no new tab should have been inserted.
        Assert.Equal(tabCountBeforeDuplicate, tabs.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithNoActiveTab_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Do not open any tabs.
        await viewModel.DuplicateBrowserTabAsync();

        // No exception; workspace pane has no tabs.
        var selectedRegion = viewModel.SelectedWorkspacePane.SelectedRegion;
        var tabs = selectedRegion?.Tabs?.ToList() ?? [];
        Assert.Empty(tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DuplicateBrowserTab_WithEmptyUrl_IsNoOp()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("", viewModel) { Id = "web-empty-url", Title = "Tab" };
        await viewModel.OpenTabAsync(tab);

        await viewModel.DuplicateBrowserTabAsync();

        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var tabs = selectedRegion.Tabs!.ToList();
        Assert.Single(tabs);
    }

    // --- RaiseOpenNewWindow: new tab insertion position ---

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RaiseOpenNewWindow_InsertsNewTabImmediatelyRightOfSourceTab()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
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

        var selectedRegion = Assert.IsType<WorkspaceRegionViewModel>(viewModel.SelectedWorkspacePane.SelectedRegion);
        var tabs = selectedRegion.Tabs!.ToList();

        var indexA = tabs.FindIndex(t => t.Id == "web-a");
        var indexNew = tabs.FindIndex(t => t is WebViewModel wv && wv.AddressBarUrl == "https://new.example.com");

        Assert.True(indexA >= 0, "Source tab A should be present");
        Assert.True(indexNew >= 0, "New tab should be present");
        Assert.Equal(indexA + 1, indexNew);
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
}
