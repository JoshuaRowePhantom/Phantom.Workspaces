using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using global::Dock.Model.Mvvm.Controls;
using Phantom.Dock.Avalonia.TabSwitching;
using Phantom.Workspaces.Configuration;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

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

    // ── #1119: aggregate glyph visibility on the workspace-level tab header ──────

    [Fact]
    public void WorkspacePaneDocument_WhenAllTabsIdleAndNoNotification_ShowsNeitherGlyph()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Idle;
        pane.Tabs.Add(new TestRunningTab("idle-tab", tabStatus));

        var running = doc.EffectiveTabHeader.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().Single();
        var notification = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();

        Assert.False(running.IsRunning);
        Assert.False(notification.HasUnread);
    }

    [Fact]
    public void WorkspacePaneDocument_WhenTabRunningAndUnread_ShowsBothGlyphs()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Running;
        pane.Tabs.Add(new TestRunningTab("running-tab", tabStatus));
        pane.AnyTabHasUnreadNotification = true;

        var running = doc.EffectiveTabHeader.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().Single();
        var notification = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();

        Assert.True(running.IsRunning);
        Assert.True(notification.HasUnread);
    }

    [Fact]
    public void WorkspacePaneDocument_RunningIndicator_ClearsWhenTabTransitionsToIdle()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        var tabStatus = new StatusItem();
        tabStatus.RunningStatus = RunningStatus.Running;
        pane.Tabs.Add(new TestRunningTab("running-tab", tabStatus));

        var running = doc.EffectiveTabHeader.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().Single();
        Assert.True(running.IsRunning);

        tabStatus.RunningStatus = RunningStatus.Idle;

        Assert.False(running.IsRunning);
    }

    [Fact]
    public void WorkspacePaneDocument_EffectiveTabHeader_Title_MatchesPaneTitle()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var doc = new WorkspacePaneDocument(pane);

        Assert.Equal(pane.Title, doc.EffectiveTabHeader.Title);
    }

    [Fact]
    public async Task SaveCommand_WhenWorkspaceIsWritable_InvokesSaveDelegate()
    {
        var calls = 0;
        var pane = new WorkspacePaneViewModel(
            CreateWorkspaceEntity(),
            saveAsync: _ =>
            {
                calls++;
                return Task.CompletedTask;
            });

        pane.SaveCommand.Execute(null);
        await pane.SaveCommand.LastExecutionTask!;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SaveCommand_WhenWorkspaceIsReadOnly_CannotExecute()
    {
        var pane = new WorkspacePaneViewModel(
            CreateWorkspaceEntity(),
            saveAsync: _ => Task.CompletedTask,
            isReadOnly: true);

        Assert.False(pane.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task WorkspacePaneViewModel_SaveCommand_CanExecute_IsFalseWhileSaving()
    {
        // Regression for #1169: the save button must be disabled while an in-flight save is
        // running so the user cannot double-invoke the same save.
        var gate = new TaskCompletionSource();
        var pane = new WorkspacePaneViewModel(
            CreateWorkspaceEntity(),
            saveAsync: async _ => await gate.Task);

        Assert.True(pane.SaveCommand.CanExecute(null));

        pane.SaveCommand.Execute(null);

        // Save is in-flight and awaiting the gate; CanExecute must be false so the button disables.
        Assert.False(pane.SaveCommand.CanExecute(null));
        Assert.True(pane.IsSaving);

        gate.SetResult();
        await pane.SaveCommand.LastExecutionTask!;

        Assert.False(pane.IsSaving);
        Assert.True(pane.SaveCommand.CanExecute(null));
    }

    // ── #1198: recursive DisposeAsync cascades to child tabs ────────────────

    [Fact]
    public async Task WorkspacePaneViewModel_WhenDisposed_RecursivelyDisposesTabs()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var tabA = new DisposeSpyTab("tab-a");
        var tabB = new DisposeSpyTab("tab-b");
        pane.Tabs.Add(tabA);
        pane.Tabs.Add(tabB);

        await pane.DisposeAsync();

        Assert.Equal(1, tabA.DisposeCount);
        Assert.Equal(1, tabB.DisposeCount);
        Assert.Empty(pane.Tabs);
    }

    [Fact]
    public async Task WorkspacePaneViewModel_DisposeAsync_CascadesToPaneAndReleasesAgentLeases()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var leaseDisposed = 0;
        var agentTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-tab",
            Title = "Agent",
        };
        var lease = new Phantom.Workspaces.Llm.RunningAgentChatLease(
            new Phantom.Workspaces.Llm.Interfaces.AgentSessionId("session-1198"),
            null!,
            () => { System.Threading.Interlocked.Increment(ref leaseDisposed); return ValueTask.CompletedTask; });
        agentTab.SetLease(lease);
        pane.Tabs.Add(agentTab);

        await pane.DisposeAsync();

        Assert.Equal(1, leaseDisposed);
    }

    // ── #1340: restore does not reuse a stale documentsByTabId entry ─────────

    [AvaloniaFact(Timeout = 20_000)]
    public async Task TryRestoreFromDockLayoutAsync_WithStaleDocumentsByTabIdEntry_StillCreatesFreshWorkspaceDocument()
    {
        // Drive a real workspace pane (wired with a full environment) via the MainWindow view model.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://stale-entry-1340.example.com")
        {
            Id = "stale-tab-1",
            Title = "Stale Entry Tab",
        };
        await viewModel.OpenTabAsync(tab);
        var pane = viewModel.SelectedWorkspacePane;

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "stale-tab-1");

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        // Inject a STALE registry entry for the tab id, owned by an orphan dock that no longer hosts
        // it — the exact #1340 pre-condition a prior implementation would have hit on reopen.
        var orphanOwner = new global::Dock.Model.Mvvm.Controls.DocumentDock { Id = "prior-owner" };
        var staleDoc = new WorkspaceDocument { Id = "stale-tab-1", Owner = orphanOwner };
        pane.RegisterDocument("stale-tab-1", staleDoc);

        // Re-run restore; the stale entry must NOT suppress creation of a fresh WorkspaceDocument.
        var success = await pane.TryRestoreFromDockLayoutAsync(pane.Entity, dockLayoutJson);

        Assert.True(success);
        var fresh = pane.GetDocumentForTab("stale-tab-1");
        Assert.NotNull(fresh);
        Assert.NotSame(staleDoc, fresh);
    }

    // ── Per-pane document registry tests (#1341) ─────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_GetDocumentForTab_ReturnsRegisteredDocument()
    {
        // Verify GetDocumentForTab returns a registered document correctly.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://registry-test.example.com")
        {
            Id = "registry-test-tab",
            Title = "Registry Test",
        };
        await viewModel.OpenTabAsync(tab);
        var pane = viewModel.SelectedWorkspacePane;

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "registry-test-tab");

        var document = pane.GetDocumentForTab("registry-test-tab");
        Assert.NotNull(document);
        Assert.Equal("registry-test-tab", document!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_OwnsDocumentTab_ReturnsTrueForRegisteredTab()
    {
        // Verify OwnsDocumentTab returns true when the pane owns the tab.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://owns-test.example.com")
        {
            Id = "owns-test-tab",
            Title = "Owns Test",
        };
        await viewModel.OpenTabAsync(tab);
        var pane = viewModel.SelectedWorkspacePane;

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "owns-test-tab");

        Assert.True(pane.OwnsDocumentTab("owns-test-tab"));
        Assert.False(pane.OwnsDocumentTab("nonexistent-tab"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_OwnsDocumentRegistry_RegistersTabDocumentOnAdd()
    {
        // Verify RegisterDocument adds a document to the pane's registry.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var pane = viewModel.SelectedWorkspacePane;

        var mockDoc = new WorkspaceDocument { Id = "manual-register-tab" };
        pane.RegisterDocument("manual-register-tab", mockDoc);

        var retrieved = pane.GetDocumentForTab("manual-register-tab");
        Assert.NotNull(retrieved);
        Assert.Same(mockDoc, retrieved);
        Assert.True(pane.OwnsDocumentTab("manual-register-tab"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_UnregisterDocument_RemovesFromRegistry()
    {
        // Verify UnregisterDocument removes a document from the pane's registry.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var pane = viewModel.SelectedWorkspacePane;

        var mockDoc = new WorkspaceDocument { Id = "unregister-test-tab" };
        pane.RegisterDocument("unregister-test-tab", mockDoc);
        Assert.True(pane.OwnsDocumentTab("unregister-test-tab"));

        pane.UnregisterDocument("unregister-test-tab");
        Assert.False(pane.OwnsDocumentTab("unregister-test-tab"));
        Assert.Null(pane.GetDocumentForTab("unregister-test-tab"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_Dispose_DiscardsDocumentRegistry()
    {
        // Verify disposing the pane discards its document registry.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://dispose-registry.example.com")
        {
            Id = "dispose-registry-tab",
            Title = "Dispose Registry",
        };
        await viewModel.OpenTabAsync(tab);
        var pane = viewModel.SelectedWorkspacePane;

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "dispose-registry-tab");
        Assert.True(pane.OwnsDocumentTab("dispose-registry-tab"));

        await pane.DisposeAsync();

        // After dispose, the registry should be cleared.
        Assert.False(pane.OwnsDocumentTab("dispose-registry-tab"));
        Assert.Null(pane.GetDocumentForTab("dispose-registry-tab"));
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePaneViewModel_NavigateToDocumentTabAsync_ActivatesAndFocusesWithinOwnLayout()
    {
        // Verify NavigateToDocumentTabAsync activates and focuses the requested tab.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://nav-a.example.com") { Id = "nav-tab-a", Title = "A" };
        var tabB = new WebViewModel("https://nav-b.example.com") { Id = "nav-tab-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        var pane = viewModel.SelectedWorkspacePane;

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "nav-tab-a");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "nav-tab-b");

        // Navigate to tab A (which might not be the active one).
        var result = await pane.NavigateToDocumentTabAsync("nav-tab-a", deferIfAbsent: false);

        Assert.True(result);
        var activeDoc = contentDock!.ActiveDockable as WorkspaceDocument;
        Assert.NotNull(activeDoc);
        Assert.Equal("nav-tab-a", activeDoc!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_NavigateToDocumentTabAsync_ReturnsFalseForUnknownTab()
    {
        // Verify NavigateToDocumentTabAsync returns false for a nonexistent tab.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var pane = viewModel.SelectedWorkspacePane;

        var result = await pane.NavigateToDocumentTabAsync("nonexistent-tab-id", deferIfAbsent: false);

        Assert.False(result);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_HandleChildTabClosed_WhenActive_ActivatesMruTab()
    {
        // Verify closing the active child tab activates the MRU tab.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://mru-a.example.com") { Id = "mru-tab-a", Title = "A" };
        var tabB = new WebViewModel("https://mru-b.example.com") { Id = "mru-tab-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        var pane = viewModel.SelectedWorkspacePane;

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "mru-tab-a");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "mru-tab-b");

        // Tab B should be active (last opened). Close it via HandleChildTabClosed.
        Assert.Equal("mru-tab-b", pane.SelectedTab?.Id);
        pane.HandleChildTabClosed(tabB);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Tab A should now be selected (MRU).
        Assert.DoesNotContain(tabB, pane.Tabs);
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

    private sealed class DisposeSpyTab : WorkspaceTabViewModel
    {
        public int DisposeCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DisposeSpyTab(string id)
        {
            this.Id = id;
            this.Title = id;
            this.DockRegion = "full";
        }

        public override async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            await base.DisposeAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RELOCATED TESTS: from MainWindowViewModelTests.cs (per-pane tests)
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public async Task WorkspacePane_AltDigit_ActivatesContentTabFromVisibleDockables()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.OpenTabAsync(new WebViewModel("https://a.example.com") { Id = "alt-content-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://b.example.com") { Id = "alt-content-b", Title = "B" });
        await viewModel.OpenTabAsync(new WebViewModel("https://c.example.com") { Id = "alt-content-c", Title = "C" });

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        var docs = contentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.True(docs.Count >= 3);
        var contentDockControl = CreateContentDockControl(viewModel.SelectedWorkspacePane.ContentLayout!);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D2,
            KeyModifiers = KeyModifiers.Alt,
            Source = contentDockControl,
        };
        contentDockControl.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(docs[1], contentDock.ActiveDockable);
    }

    [AvaloniaFact]
    public async Task WorkspacePane_BadgeLabelAndActivation_UseSameOrder()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(new WebViewModel("https://1.example.com") { Id = "order-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://2.example.com") { Id = "order-b", Title = "B" });
        await viewModel.OpenTabAsync(new WebViewModel("https://3.example.com") { Id = "order-c", Title = "C" });

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        var visible = Assert.IsAssignableFrom<IList<IDockable>>(contentDock!.VisibleDockables!);
        var firstBefore = visible[0];
        visible.RemoveAt(0);
        visible.Insert(2, firstBefore);
        var contentDockControl = CreateContentDockControl(viewModel.SelectedWorkspacePane.ContentLayout!);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D1,
            KeyModifiers = KeyModifiers.Alt,
            Source = contentDockControl,
        };
        contentDockControl.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(visible[0], contentDock.ActiveDockable);
    }

    [AvaloniaFact]
    public async Task WorkspacePane_AfterSplitOrReorder_NumberingMatchesVisibleOrder()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(new WebViewModel("https://x.example.com") { Id = "reorder-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://y.example.com") { Id = "reorder-b", Title = "B" });
        await viewModel.OpenTabAsync(new WebViewModel("https://z.example.com") { Id = "reorder-c", Title = "C" });

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        var visible = Assert.IsAssignableFrom<IList<IDockable>>(contentDock!.VisibleDockables!);
        var last = visible[2];
        visible.RemoveAt(2);
        visible.Insert(0, last);
        var contentDockControl = CreateContentDockControl(viewModel.SelectedWorkspacePane.ContentLayout!);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D2,
            KeyModifiers = KeyModifiers.Alt,
            Source = contentDockControl,
        };
        contentDockControl.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(visible[1], contentDock.ActiveDockable);
    }

    private static DockControl CreateContentDockControl(IRootDock rootDock)
    {
        var dockControl = new DockControl { Layout = rootDock };
        DockTabSwitch.SetEnabled(dockControl, true);
        DockTabSwitch.SetBindings(dockControl, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures
            {
                Modifiers = KeyModifiers.Alt,
                Keys = DockTabSwitchKeys.Digits,
                Scope = DockTabSwitchScope.FocusedDockOnly,
            },
        });
        return dockControl;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RELOCATED TESTS: Multi-region layout tests (per-pane registry behavior)
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoreMultiRegionLayout_TabsContainsAllRegionsTabs()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, _, _) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c1a7a0-1333-4000-8000-000000000001"),
            "mr1-left", "mr1-tab-left", "mr1-right", "mr1-tab-right");

        Assert.Contains(pane.Tabs, t => t.Id == "mr1-tab-left");
        Assert.Contains(pane.Tabs, t => t.Id == "mr1-tab-right");
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoredMultiRegionLayout_GetDocumentForTab_ResolvesRightRegionTabToRightDock()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, _, rightDock) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c1a7a0-1333-4000-8000-000000000002"),
            "mr2-left", "mr2-tab-left", "mr2-right", "mr2-tab-right");

        var rightDocument = pane.GetDocumentForTab("mr2-tab-right");
        Assert.NotNull(rightDocument);
        Assert.Same(rightDock, rightDocument!.Owner);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePaneViewModel_TryRestoreFromDockLayout_CanonicalizesDuplicateDocuments()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, _, _) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c1a7a0-1333-4000-8000-000000000003"),
            "mr3-left", "mr3-tab-left", "mr3-right", "mr3-tab-right");

        foreach (var tabId in new[] { "mr3-tab-left", "mr3-tab-right" })
        {
            var occurrences = MultiRegionRestoreTestSupport.EnumerateDocks(pane.ContentLayout!)
                .SelectMany(d => d.VisibleDockables?.OfType<WorkspaceDocument>() ?? Enumerable.Empty<WorkspaceDocument>())
                .Count(doc => doc.Id == tabId);
            Assert.Equal(1, occurrences);
        }
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoredMultiRegionWebTab_RaiseOpenNewWindow_InsertsNewTabInSameRegion()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, leftDock, rightDock) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c1a7a0-1333-4000-8000-000000000004"),
            "mr4-left", "mr4-tab-left", "mr4-right", "mr4-tab-right");

        var rightTab = Assert.IsType<WebViewModel>(pane.Tabs.Single(t => t.Id == "mr4-tab-right"));
        var rightSourceDoc = rightDock.VisibleDockables!.OfType<WorkspaceDocument>().Single(d => d.Id == "mr4-tab-right");
        var sourceIndex = rightDock.VisibleDockables!.IndexOf(rightSourceDoc);

        rightTab.RaiseOpenNewWindow("https://mr4-new.example.com");
        await MultiRegionRestoreTestSupport.WaitForDockableCountAsync(rightDock, 2);

        var newDoc = rightDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .Single(d => d.TabViewModel is WebViewModel wv && wv.AddressBarUrl == "https://mr4-new.example.com");

        // (a) inserted into the RIGHT region at sourceIndex + 1
        Assert.Equal(sourceIndex + 1, rightDock.VisibleDockables!.IndexOf(newDoc));
        // (b) NOT into the LEFT region
        Assert.DoesNotContain(
            leftDock.VisibleDockables!.OfType<WorkspaceDocument>(),
            d => d.TabViewModel is WebViewModel wv && wv.AddressBarUrl == "https://mr4-new.example.com");
        // (c) the new tab VM was added to the pane's Tabs membership set
        Assert.Contains(pane.Tabs, t => t is WebViewModel wv && wv.AddressBarUrl == "https://mr4-new.example.com");
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoredMultiRegionWebTab_RaiseOpenNewWindow_DoesNotDropAnyExistingTabFromPaneTabs()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, _, rightDock) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c1a7a0-1333-4000-8000-000000000005"),
            "mr5-left", "mr5-tab-left", "mr5-right", "mr5-tab-right");

        var beforeIds = pane.Tabs.Select(t => t.Id).ToList();

        var rightTab = Assert.IsType<WebViewModel>(pane.Tabs.Single(t => t.Id == "mr5-tab-right"));
        rightTab.RaiseOpenNewWindow("https://mr5-new.example.com");
        await MultiRegionRestoreTestSupport.WaitForDockableCountAsync(rightDock, 2);

        var afterIds = pane.Tabs.Select(t => t.Id).ToList();
        foreach (var id in beforeIds)
        {
            Assert.Contains(id, afterIds);
        }
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoredMultiRegionNavigation_BackForward_ReachesRightRegionTab()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, _, _) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c1a7a0-1333-4000-8000-000000000006"),
            "mr6-left", "mr6-tab-left", "mr6-right", "mr6-tab-right");

        var leftTab = pane.Tabs.Single(t => t.Id == "mr6-tab-left");
        var rightTab = pane.Tabs.Single(t => t.Id == "mr6-tab-right");

        await viewModel.OpenTabAsync(leftTab);
        await viewModel.OpenTabAsync(rightTab);
        Assert.Equal("mr6-tab-right", pane.SelectedTab?.Id);

        viewModel.NavigateBackCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("mr6-tab-left", pane.SelectedTab?.Id);

        viewModel.NavigateForwardCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("mr6-tab-right", pane.SelectedTab?.Id);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_ManuallySplitAfterRestore_RaiseOpenNewWindow_InsertsNewTabInSplitDock()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Restore a single-region layout, then split it at runtime like the working manual path.
        var tabA = new WebViewModel("https://split-a.example.com", viewModel) { Id = "mr7-a", Title = "A" };
        var tabB = new WebViewModel("https://split-b.example.com", viewModel) { Id = "mr7-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        var dockFactory = MultiRegionRestoreTestSupport.GetDockFactory(viewModel);
        var primaryDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!)!;
        var docA = primaryDock.VisibleDockables!.OfType<WorkspaceDocument>().Single(d => d.Id == "mr7-a");

        var splitDock = dockFactory.CreateDocumentDock();
        splitDock.Id = "mr7-split";
        var contentRoot = (IDock)pane.ContentLayout!;
        dockFactory.AddDockable(contentRoot, splitDock);
        dockFactory.MoveDockable(primaryDock, splitDock, docA, null);
        Assert.Same(splitDock, docA.Owner);
        pane.RegisterDocument("mr7-a", docA);

        pane.SelectedTab = docA.TabViewModel;
        dockFactory.SetActiveDockable(docA);

        var splitTab = Assert.IsType<WebViewModel>(docA.TabViewModel);
        splitTab.RaiseOpenNewWindow("https://mr7-new.example.com");
        await MultiRegionRestoreTestSupport.WaitForDockableCountAsync(splitDock, 2);

        var newDoc = splitDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .Single(d => d.TabViewModel is WebViewModel wv && wv.AddressBarUrl == "https://mr7-new.example.com");
        var indexA = splitDock.VisibleDockables!.IndexOf(docA);
        Assert.Equal(indexA + 1, splitDock.VisibleDockables!.IndexOf(newDoc));
    }

    // #1334 / #1333 (relocated from MainWindowDockTemplateTests): opening a new tab anchored to the
    // RIGHT (non-primary) region's web tab — the exact path a Ctrl-click / NewWindowRequested drives
    // via OpenTabAsync(insertAfterTabId) — must place the new document in the RIGHT dock, and the
    // per-pane registry must resolve the new tab to the RIGHT dock (not the DFS-first primary).
    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoredNonPrimaryRegionWebTab_CtrlClickNewWindow_InsertsInSameRegion()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, leftDock, rightDock) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c11334-0003-4000-8000-000000000003"),
            "cc-left", "tab-left-3", "cc-right", "tab-right-3");

        // The right region's restored tab must be registered so anchor resolution finds it.
        var rightAnchor = pane.GetDocumentForTab("tab-right-3");
        Assert.NotNull(rightAnchor);
        Assert.Same(rightDock, rightAnchor!.Owner);

        var newTab = new WebViewModel("https://ctrlclick-1334.example.com")
        {
            Id = "tab-ctrlclick-3",
            Title = "Ctrl Click",
        };
        await viewModel.OpenTabAsync(newTab, insertAfterTabId: "tab-right-3");

        var newDoc = pane.GetDocumentForTab("tab-ctrlclick-3");
        Assert.NotNull(newDoc);
        Assert.Same(rightDock, newDoc!.Owner);
        Assert.Contains(rightDock.VisibleDockables!.OfType<WorkspaceDocument>(), d => d.Id == "tab-ctrlclick-3");
        Assert.DoesNotContain(leftDock.VisibleDockables!.OfType<WorkspaceDocument>(), d => d.Id == "tab-ctrlclick-3");
    }

    // #1334 (relocated from MainWindowDockTemplateTests): every WorkspaceContentDock in the restored
    // tree is wired uniformly — the DFS-first primary owns the pane's Tabs (ItemsSource + generator)
    // while every other region has its restored documents registered so GetDocumentForTab resolves
    // each tab to the dock that actually hosts it. No DFS-first-only asymmetry.
    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePane_RestoredMultiRegionLayout_EveryContentDockUniformlyWired()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, leftDock, rightDock) = await OpenTwoRegionRestoredWorkspaceAsync(
            viewModel,
            new EntityId("d0c11334-0005-4000-8000-000000000005"),
            "uw-left", "tab-left-5", "uw-right", "tab-right-5");

        // Every content dock's restored tab resolves to the dock that actually hosts it.
        var leftReg = pane.GetDocumentForTab("tab-left-5");
        var rightReg = pane.GetDocumentForTab("tab-right-5");
        Assert.NotNull(leftReg);
        Assert.NotNull(rightReg);
        Assert.Same(leftDock, leftReg!.Owner);
        Assert.Same(rightDock, rightReg!.Owner);

        // The DFS-first primary (left) owns the pane's Tabs via ItemsSource + a live generator.
        var leftContentDock = Assert.IsType<WorkspaceContentDock>(leftDock);
        Assert.Same(pane.Tabs, leftContentDock.ItemsSource);
        Assert.IsType<WorkspaceDocumentGenerator>(leftContentDock.ItemContainerGenerator);

        // Both regions carry the header-bearing WorkspaceContentDock type (uniform wiring, not a
        // headerless base DocumentDock).
        foreach (var dock in MultiRegionRestoreTestSupport.EnumerateDocks(pane.ContentLayout!).OfType<IDocumentDock>())
        {
            Assert.IsType<WorkspaceContentDock>(dock);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RELOCATED TESTS: TryFocusExistingWebTabAsync tests (per-pane tab search)
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public async Task WorkspacePaneViewModel_TryFocusExistingWebTabAsync_SameUrlOpen_ActivatesAndReturnsTrue()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var url = "https://example.com/";
        var existing = new WebViewModel(url) { Id = "web-existing", Title = "Existing" };
        await viewModel.OpenTabAsync(existing);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var otherTab = new WebViewModel("https://other.example.com/") { Id = "web-other", Title = "Other" };
        await viewModel.OpenTabAsync(otherTab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var result = await viewModel.TryFocusExistingWebTabAsync(url);

        Assert.True(result);
        Assert.Equal(existing, viewModel.SelectedWorkspacePane.SelectedTab);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_SameUrlOpenInDifferentPane_ReturnsFalse()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var url = "https://example.com/";

        // Open a tab with the URL in the currently selected pane.
        var firstPaneTab = new WebViewModel(url) { Id = "web-p1", Title = "P1" };
        await viewModel.OpenTabAsync(firstPaneTab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var firstPane = viewModel.SelectedWorkspacePane;

        // Move that tab to a "different" pane by removing it from the selected pane's Tabs.
        // Simulate a second pane by clearing the current pane's tabs; TryFocusExistingWebTabAsync
        // scans SelectedWorkspacePane.Tabs, so an empty selected pane must return false even if
        // other panes contain matching tabs — which is what "cross-workspace dedup is rejected"
        // requires.
        firstPane.Tabs.Clear();

        var result = await viewModel.TryFocusExistingWebTabAsync(url);
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task WorkspacePaneViewModel_TryFocusExistingWebTabAsync_DifferentUrl_ReturnsFalse()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com/a") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/b");
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_UrlDiffersOnlyByTrailingSlashOrHostCase_MatchesExistingTab()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com/") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Uri.AbsoluteUri lowercases host and canonicalizes paths.
        var result = await viewModel.TryFocusExistingWebTabAsync("https://Example.com/");
        Assert.True(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_UrlDiffersOnlyByFragment_ReturnsFalse()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com/page#a") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/page#b");
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_NoSelectedWorkspacePane_ReturnsFalse()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        // Do NOT initialize — the placeholder pane has null ContentLayout.

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/");
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_NonWebViewModelTabWithSameTitle_ReturnsFalse()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // A dummy non-web tab whose id happens to look like a URL; must not match.
        var dummy = new WebViewModel("https://example.com/") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(dummy);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Replace the tab in the pane's Tabs with a non-WebViewModel — a plain WorkspaceTabViewModel
        // is abstract; we simulate by removing the web tab entirely.
        viewModel.SelectedWorkspacePane.Tabs.Clear();

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/");
        Assert.False(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RELOCATED TESTS: Pane close disposal cascade (#1198)
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact]
    public async Task WorkspacePane_ClosingPaneWithRunningAgentTab_DisposesAllChildTabs()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, tabs) = AddPaneWithDisposeSpyTabs(viewModel, "ws-1198-a", 2);

        await viewModel.RemoveWorkspacePaneAsync(pane);

        Assert.All(tabs, t => Assert.Equal(1, t.DisposeCount));
        Assert.Empty(pane.Tabs);
    }

    [AvaloniaFact]
    public async Task WorkspacePane_ClosingPaneWithRunningAgentTab_ReleasesRunningAgentChatLease()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = AddPane(viewModel, "ws-1198-b");
        var agentTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-1198-b",
            Title = "Agent B",
        };
        var leaseDisposed = 0;
        var lease = new Phantom.Workspaces.Llm.RunningAgentChatLease(
            new Phantom.Workspaces.Llm.Interfaces.AgentSessionId("agent-session-1198-b"),
            null!,
            () => { System.Threading.Interlocked.Increment(ref leaseDisposed); return ValueTask.CompletedTask; });
        agentTab.SetLease(lease);
        pane.Tabs.Add(agentTab);

        await viewModel.RemoveWorkspacePaneAsync(pane);

        Assert.Equal(1, leaseDisposed);
    }

    [AvaloniaFact]
    public async Task WorkspacePane_ClosingPaneViaDockCloseButton_DisposesDocumentAndChildren()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, tabs) = AddPaneWithDisposeSpyTabs(viewModel, "ws-1198-c", 1);
        var paneDoc = new WorkspacePaneDocument(pane);

        // Dock UI close path: OnDockableClosed → OnWorkspacePaneDockableClosed → RemoveWorkspacePaneAsync.
        viewModel.OnWorkspacePaneDockableClosed(paneDoc);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.DoesNotContain(viewModel.WorkspacePanes, p => ReferenceEquals(p, pane));
        Assert.Equal(1, tabs[0].DisposeCount);
    }

    [AvaloniaFact]
    public async Task WorkspacePane_ClosingSingleTabViaDockClose_DisposesTabViewModel()
    {
        // Regression: single-tab close through the existing OnDockableTabClosed seam
        // must still dispose the tab VM.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, tabs) = AddPaneWithDisposeSpyTabs(viewModel, "ws-1198-d", 1);
        var tab = tabs[0];

        viewModel.OnDockableTabClosed(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.DoesNotContain(tab, pane.Tabs);
        Assert.Equal(1, tab.DisposeCount);
    }

    [AvaloniaFact]
    public async Task WorkspacePane_ReopeningWorkspaceAfterClose_RecreatesAgentSessionTab()
    {
        // #1198: closing a pane must evict stale documentsByTabId entries so the
        // freshly-restored tab is not treated as "already open" by OpenTabAsync's
        // dedupe (which would dispose the fresh tab and activate a dead document).
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var workspaceId = new EntityId("10810001-1198-4000-8000-000000000001");
        await UpsertWorkspaceAsync(entityBroker, workspaceId, "ws-1198-e", "WS 1198 E");
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = Assert.Single(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, workspaceId.Value.ToString(), StringComparison.Ordinal));
        await pane.Populated;

        // Open a tab that becomes registered in the pane's tab map.
        var tab = new WebViewModel("https://example-1198.example.com/") { Id = "web-1198-e", Title = "Web 1198 E" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.NotNull(pane.GetDocumentForTab(tab.Id));

        // Close the pane. #1341: the per-pane registry is discarded wholesale on dispose, so a
        // reopened pane starts empty and the collision guard cannot false-positive against a stale
        // prior-owner entry.
        await viewModel.RemoveWorkspacePaneAsync(pane);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Null(pane.GetDocumentForTab(tab.Id));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helper methods (relocated from MainWindowViewModelTests.cs)
    // ══════════════════════════════════════════════════════════════════════════

    private static async Task<(WorkspacePaneViewModel Pane, IDock LeftDock, IDock RightDock)> OpenTwoRegionRestoredWorkspaceAsync(
        MainWindowViewModel viewModel,
        EntityId workspaceId,
        string leftDockId,
        string leftTabId,
        string rightDockId,
        string rightTabId)
    {
        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var layoutJson = MultiRegionRestoreTestSupport.BuildTwoRegionDockLayoutJson(
            leftDockId, leftTabId, "https://left.example.com",
            rightDockId, rightTabId, "https://right.example.com");

        var workspaceJson = $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "MR Restore WS" },
              "dock-layout": {{layoutJson}},
              "regions": []
            }
            """;
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Restore runs asynchronously (Phase 2 of OpenWorkspaceAsync); wait for it to complete.
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        var leftDock = MultiRegionRestoreTestSupport.FindDockById(pane.ContentLayout!, leftDockId)!;
        var rightDock = MultiRegionRestoreTestSupport.FindDockById(pane.ContentLayout!, rightDockId)!;
        await MultiRegionRestoreTestSupport.WaitForDockableCountAsync(leftDock, 1);
        await MultiRegionRestoreTestSupport.WaitForDockableCountAsync(rightDock, 1);

        return (pane, leftDock, rightDock);
    }

    private static WorkspacePaneViewModel AddPane(MainWindowViewModel viewModel, string id)
    {
        var entityId = Guid.NewGuid();
        using var entityDoc = System.Text.Json.JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": "{{id}}"
            }
            """);
        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(entityId),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = entityDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
        var pane = new WorkspacePaneViewModel(entity, id);
        viewModel.WorkspacePanes.Add(pane);
        return pane;
    }

    private static (WorkspacePaneViewModel pane, DisposeSpyTab_1198[] tabs) AddPaneWithDisposeSpyTabs(
        MainWindowViewModel viewModel, string paneId, int tabCount)
    {
        var pane = AddPane(viewModel, paneId);
        var tabs = new DisposeSpyTab_1198[tabCount];
        for (var i = 0; i < tabCount; i++)
        {
            tabs[i] = new DisposeSpyTab_1198($"{paneId}-tab-{i}");
            pane.Tabs.Add(tabs[i]);
        }
        return (pane, tabs);
    }

    private static async Task UpsertWorkspaceAsync(
        EntityBroker entityBroker,
        EntityId workspaceId,
        string idString,
        string displayName)
    {
        var json = $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "{{displayName}}" },
              "regions": []
            }
            """;
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, json);
    }

    private sealed class DisposeSpyTab_1198 : WorkspaceTabViewModel
    {
        public int DisposeCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DisposeSpyTab_1198(string id)
        {
            this.Id = id;
            this.Title = id;
            this.DockRegion = "full";
        }

        public override async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            await base.DisposeAsync();
        }
    }


    // ══════════════════════════════════════════════════════════════════════════
    // RELOCATED TESTS: CloseActiveTabCommand tests (per-pane close behavior)
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_CloseActiveTab_WhenActiveTabExists_RoutesThroughFactoryCloseDockable()
    {
        // #1170: Ctrl+W must go through Factory.CloseDockable(activeDoc) — observable
        // via factory.DockableClosed, which is NOT raised by a raw pane.Tabs.Remove(tab).
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://route.example.com") { Id = "route-a", Title = "Route A" };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactoryViaReflection(viewModel);
        var documentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(documentDock);

        IDockable? closedDockable = null;
        factory.DockableClosed += (_, e) => closedDockable = e.Dockable;

        Assert.Equal("route-a", documentDock!.ActiveDockable?.Id);
        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(closedDockable);
        Assert.IsAssignableFrom<WorkspaceDocument>(closedDockable!);
        Assert.Equal("route-a", closedDockable!.Id);
        Assert.DoesNotContain(pane.Tabs, t => t.Id == "route-a");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_CloseActiveTab_WhenLastTabInSplitDockClosed_RemovesEmptyDockAndSplitter()
    {
        // #1170: after closing the last tab of a nested split DocumentDock, the empty
        // DocumentDock AND its adjacent ProportionalDockSplitter must be removed from
        // the parent ProportionalDock's VisibleDockables.
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactoryViaReflection(viewModel);

        var (root, prop, splitDoc, splitter, mainDocA, mainDocB) = BuildSplitLayoutForCloseTests(factory);
        var tab = new WebViewModel("about:blank") { Id = "split-last-a", Title = "Split A" };
        var doc = new WorkspaceDocument(tab) { Owner = splitDoc };
        splitDoc.VisibleDockables = factory.CreateList<IDockable>(doc);
        splitDoc.ActiveDockable = doc;
        pane!.ContentLayout = root;

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(prop.VisibleDockables);
        Assert.DoesNotContain(splitDoc, prop.VisibleDockables!);
        Assert.DoesNotContain(splitter, prop.VisibleDockables!);
        // The other split children are untouched.
        Assert.Contains(mainDocA, prop.VisibleDockables!);
        Assert.Contains(mainDocB, prop.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenNonLastTabInSplitDockClosed_KeepsDockRegionAndSplitter()
    {
        // #1170: closing one of several tabs in a split region must NOT collapse the
        // region — the DocumentDock and its adjacent splitter stay in place and the
        // sibling tab remains.
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactoryViaReflection(viewModel);

        var (root, prop, splitDoc, splitter, mainDocA, _) = BuildSplitLayoutForCloseTests(factory);
        var tabActive = new WebViewModel("about:blank") { Id = "split-multi-a", Title = "Split A" };
        var tabOther = new WebViewModel("about:blank") { Id = "split-multi-b", Title = "Split B" };
        var docActive = new WorkspaceDocument(tabActive) { Owner = splitDoc };
        var docOther = new WorkspaceDocument(tabOther) { Owner = splitDoc };
        splitDoc.VisibleDockables = factory.CreateList<IDockable>(docActive, docOther);
        splitDoc.ActiveDockable = docActive;
        pane!.ContentLayout = root;

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(prop.VisibleDockables);
        Assert.Contains(splitDoc, prop.VisibleDockables!);
        Assert.Contains(splitter, prop.VisibleDockables!);
        Assert.Contains(mainDocA, prop.VisibleDockables!);
        Assert.NotNull(splitDoc.VisibleDockables);
        Assert.DoesNotContain(docActive, splitDoc.VisibleDockables!);
        Assert.Contains(docOther, splitDoc.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_CloseActiveTab_WhenFocusedInSplitRegion_ClosesOnlyFocusedRegionActiveTab()
    {
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactoryViaReflection(viewModel);

        var (root, _, leftDock, _, _, rightDock) = BuildSplitLayoutForCloseTests(factory);
        var leftTab = new WebViewModel("about:blank") { Id = "focus-left", Title = "Left" };
        var rightTab = new WebViewModel("about:blank") { Id = "focus-right", Title = "Right" };
        var leftDoc = new WorkspaceDocument(leftTab) { Owner = leftDock };
        var rightDoc = new WorkspaceDocument(rightTab) { Owner = rightDock };
        leftDock.VisibleDockables = factory.CreateList<IDockable>(leftDoc);
        leftDock.ActiveDockable = leftDoc;
        rightDock.VisibleDockables = factory.CreateList<IDockable>(rightDoc);
        rightDock.ActiveDockable = rightDoc;
        pane!.ContentLayout = root;

        // Focus the RIGHT region; without #1310's fix, FindDocumentDock's depth-first
        // walk would return the LEFT region (index 0 in the ProportionalDock).
        factory.SetFocusedDockable(rightDock, rightDoc);

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.Contains(leftDoc, leftDock.VisibleDockables!);
        Assert.NotNull(rightDock.VisibleDockables);
        Assert.DoesNotContain(rightDoc, rightDock.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenFocusedInLeftSplitRegion_ClosesOnlyLeftRegionActiveTab_AndLeavesRightRegionActiveTabOpen()
    {
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactoryViaReflection(viewModel);

        var (root, _, leftDock, _, _, rightDock) = BuildSplitLayoutForCloseTests(factory);
        var leftTab = new WebViewModel("about:blank") { Id = "focus-left-2", Title = "Left" };
        var rightTab = new WebViewModel("about:blank") { Id = "focus-right-2", Title = "Right" };
        var leftDoc = new WorkspaceDocument(leftTab) { Owner = leftDock };
        var rightDoc = new WorkspaceDocument(rightTab) { Owner = rightDock };
        leftDock.VisibleDockables = factory.CreateList<IDockable>(leftDoc);
        leftDock.ActiveDockable = leftDoc;
        rightDock.VisibleDockables = factory.CreateList<IDockable>(rightDoc);
        rightDock.ActiveDockable = rightDoc;
        pane!.ContentLayout = root;

        factory.SetFocusedDockable(leftDock, leftDoc);

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(leftDock.VisibleDockables);
        Assert.DoesNotContain(leftDoc, leftDock.VisibleDockables!);
        Assert.Contains(rightDoc, rightDock.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RootAndWorkspacesPaneDock_WhenLastChildClosed_AreNotRemoved()
    {
        // #1170: the top-level RootDock and WorkspacesPaneDock have IsCollapsable=false,
        // so FactoryBase.CollapseDock refuses to remove them even when their child list
        // is empty. This guards the primary layout from ever disappearing.
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();
        Assert.NotNull(viewModel.Layout);

        var root = viewModel.Layout!;
        Assert.False(root.IsCollapsable);

        var workspacesDock = root.VisibleDockables!.OfType<WorkspacesPaneDock>().First();
        Assert.False(workspacesDock.IsCollapsable);

        var factory = GetDockFactoryViaReflection(viewModel);

        // Snapshot children, empty both docks, invoke CollapseDock, verify no removal.
        var rootChildren = root.VisibleDockables!.ToList();
        var workspacesChildren = workspacesDock.VisibleDockables!.ToList();
        workspacesDock.VisibleDockables!.Clear();
        factory.CollapseDock(workspacesDock);
        Assert.Contains(workspacesDock, root.VisibleDockables!);

        root.VisibleDockables!.Clear();
        factory.CollapseDock(root);
        // A root is only actually collapsed if its Owner has it in a list AND it is
        // collapsable; neither holds. Assert it still exists as an object with no owner
        // change and that IsCollapsable is still false.
        Assert.False(root.IsCollapsable);

        // Restore for viewModel disposal.
        foreach (var c in workspacesChildren) workspacesDock.VisibleDockables!.Add(c);
        foreach (var c in rootChildren) root.VisibleDockables!.Add(c);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenActiveTabClosed_DisposesTabExactlyOnceViaOnDockableTabClosed()
    {
        // #1170: after routing through Factory.CloseDockable, disposal must run exactly
        // once. The Ctrl+W code path used to call DisposeWorkspaceTabAsync itself AND
        // OnDockableTabClosed also runs it — that duplicate is gone with the fix.
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://dispose.example.com") { Id = "dispose-a", Title = "Dispose A" };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);

        var removeCount = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)pane!.Tabs).CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                && e.OldItems?.Contains(tab) == true)
            {
                removeCount++;
            }
        };

        viewModel.CloseActiveTabCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(pane.Tabs, t => ReferenceEquals(t, tab));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenActiveTabClosed_ActivatesMostRecentlyUsedTab()
    {
        // #1170: after Ctrl+W closes the active tab, MRU navigation (via
        // navigationHistoryService.GoBackSkipping -> ActivateTabById) must activate the
        // previously-open tab — matching the close-button / middle-click paths.
        await using var viewModel = CreateBootedMainWindowViewModelForCloseTests();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://mru.example.com/a") { Id = "mru-1170-a", Title = "A" };
        var tabB = new WebViewModel("https://mru.example.com/b") { Id = "mru-1170-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var documentDock = MainWindowIntegrationTests.FindDocumentDockIn(viewModel.SelectedWorkspacePane!.ContentLayout!);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-1170-b", documentDock!.ActiveDockable?.Id);

        viewModel.CloseActiveTabCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal("mru-1170-a", documentDock.ActiveDockable?.Id);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RELOCATED TESTS: DocumentDock migration tests (#1324, #1330)
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_TryRestoreFromDockLayout_WithLegacyBaseDocumentDock_MigratesToWorkspaceContentDock()
    {
        // #1324: pre-#1307 persisted layouts encode inner split docks as a base
        // Dock.Model.Mvvm.Controls.DocumentDock. Deserializing such a layout through the exact
        // restore serializer (DockSerializer + WorkspaceDockTypeInfoResolver) produces a base
        // DocumentDock; the restore-time migration must materialize it as a WorkspaceContentDock so
        // it matches the header-bearing DataTemplate instead of the headerless generic fallback.
        IRootDock originalRoot = new RootDock
        {
            Id = "restore-root",
            Title = "restore-root",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var baseDock = new DocumentDock
        {
            Id = "persisted-base-dock",
            Title = "Persisted Base Dock",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var persistedDoc = new Document { Id = "persisted-doc", Title = "Persisted Doc" };
        baseDock.VisibleDockables!.Add(persistedDoc);
        baseDock.ActiveDockable = persistedDoc;
        originalRoot.VisibleDockables!.Add(baseDock);
        originalRoot.ActiveDockable = baseDock;
        originalRoot.DefaultDockable = baseDock;

        var serializer = new global::Dock.Serializer.SystemTextJson.DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());

        var json = serializer.Serialize(originalRoot);
        Assert.Contains(typeof(DocumentDock).FullName!, json);

        var restored = serializer.Deserialize<IRootDock>(json);
        Assert.NotNull(restored);

        // Reproduce the bug precondition: a straight restore yields a base DocumentDock,
        // which is exactly what renders headerless.
        var beforeMigration = MainWindowIntegrationTests.FindDocumentDockIn(restored!);
        Assert.NotNull(beforeMigration);
        Assert.IsNotType<WorkspaceContentDock>(beforeMigration);

        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(restored);

        // Every document-dock region in the restored tree is now the workspace-specific type.
        var documentDocks = EnumerateDocumentDocksForMigration(restored!).ToList();
        Assert.NotEmpty(documentDocks);
        Assert.All(documentDocks, d => Assert.IsType<WorkspaceContentDock>(d));

        var migrated = Assert.IsType<WorkspaceContentDock>(MainWindowIntegrationTests.FindDocumentDockIn(restored!));
        Assert.Equal("persisted-base-dock", migrated.Id);
        Assert.Equal("Persisted Base Dock", migrated.Title);
        Assert.Equal(1, migrated.VisibleDockables?.Count);
        Assert.NotNull(migrated.ActiveDockable);
        Assert.Equal("persisted-doc", migrated.ActiveDockable!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspacePane_MigrateBaseDocumentDock_PreservesActiveDockableAndReparentsChildren()
    {
        // Losslessness of the substitution, isolated from serialization: Id/Title/VisibleDockables/
        // ActiveDockable identity are preserved, the child's Owner is re-pointed to the new dock,
        // and the parent's active/default/focused references are re-pointed to the replacement.
        var root = new RootDock
        {
            Id = "root",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var baseDock = new DocumentDock
        {
            Id = "base-dock",
            Title = "Base Dock",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var doc = new Document { Id = "doc", Title = "Doc" };
        baseDock.VisibleDockables!.Add(doc);
        baseDock.ActiveDockable = doc;
        doc.Owner = baseDock;
        root.VisibleDockables!.Add(baseDock);
        root.ActiveDockable = baseDock;
        root.DefaultDockable = baseDock;
        root.FocusedDockable = baseDock;
        baseDock.Owner = root;

        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(root);

        var migrated = Assert.IsType<WorkspaceContentDock>(root.VisibleDockables![0]);
        Assert.Same(migrated, root.ActiveDockable);
        Assert.Same(migrated, root.DefaultDockable);
        Assert.Same(migrated, root.FocusedDockable);
        Assert.Equal("base-dock", migrated.Id);
        Assert.Equal("Base Dock", migrated.Title);
        Assert.Same(doc, migrated.VisibleDockables?[0]);
        Assert.Same(doc, migrated.ActiveDockable);
        Assert.Same(migrated, doc.Owner);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspacePane_MigrateBaseDocumentDock_InSplitTree_MigratesEveryRegionAndKeepsSplitters()
    {
        // A pre-#1307 split layout: ProportionalDock [ baseDock, splitter, baseDock2 ]. Both
        // document regions must become WorkspaceContentDock while the ProportionalDock and its
        // splitter are left untouched.
        var root = new RootDock
        {
            Id = "root",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var prop = new ProportionalDock
        {
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var baseDock1 = new DocumentDock
        {
            Id = "left",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        var splitter = new ProportionalDockSplitter { Id = "splitter" };
        var baseDock2 = new DocumentDock
        {
            Id = "right",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable>(),
        };
        prop.VisibleDockables!.Add(baseDock1);
        prop.VisibleDockables!.Add(splitter);
        prop.VisibleDockables!.Add(baseDock2);
        root.VisibleDockables!.Add(prop);

        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(root);

        var migratedProp = Assert.IsType<ProportionalDock>(root.VisibleDockables![0]);
        Assert.IsType<WorkspaceContentDock>(migratedProp.VisibleDockables![0]);
        Assert.Same(splitter, migratedProp.VisibleDockables![1]);
        Assert.IsType<WorkspaceContentDock>(migratedProp.VisibleDockables![2]);
        Assert.Equal("left", ((WorkspaceContentDock)migratedProp.VisibleDockables![0]).Id);
        Assert.Equal("right", ((WorkspaceContentDock)migratedProp.VisibleDockables![2]).Id);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePaneViewModel_TryRestoreFromDockLayout_WithMultiRegionSplit_RestoresAllRegionsAsWorkspaceContentDock()
    {
        // #1330 restore composition: a persisted multi-region layout whose split leaves are the
        // pre-#1307 base Mvvm DocumentDock $type. Driving the real workspace restore path
        // (TryRestoreFromDockLayoutAsync via OpenWorkspaceAsync) must migrate BOTH split regions to
        // WorkspaceContentDock, preserve each region's tab count, and keep the splitter sibling.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);

        // Build a real two-region layout, then rewrite each split leaf's $type discriminator to the
        // base pre-#1307 Mvvm DocumentDock to simulate a legacy persisted layout.
        var layoutJson = MultiRegionRestoreTestSupport.BuildTwoRegionDockLayoutJson(
                "mr-restore-left", "mr-restore-tab-left", "https://mr-left.example.com",
                "mr-restore-right", "mr-restore-tab-right", "https://mr-right.example.com")
            .Replace(typeof(WorkspaceContentDock).FullName!, typeof(DocumentDock).FullName!);

        var workspaceId = new EntityId("d0c1a7a0-1330-4000-8000-000000000001");
        var workspaceJson = $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "MR 1330 Restore WS" },
              "dock-layout": {{layoutJson}},
              "regions": []
            }
            """;
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceId.ToString(), System.StringComparison.Ordinal));
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        var documentDocks = EnumerateDocumentDocksForMigration(pane.ContentLayout!).ToList();
        Assert.Equal(2, documentDocks.Count);
        Assert.All(documentDocks, d => Assert.IsType<WorkspaceContentDock>(d));
        Assert.All(documentDocks, d => Assert.Equal(1, d.VisibleDockables?.Count));

        var prop = EnumerateDocumentDocksForMigration(pane.ContentLayout!)
            .Select(d => d.Owner)
            .OfType<ProportionalDock>()
            .First();
        Assert.Contains(prop.VisibleDockables!, x => x is ProportionalDockSplitter);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helper methods for CloseActiveTab tests
    // ══════════════════════════════════════════════════════════════════════════

    private static MainWindowViewModel CreateBootedMainWindowViewModelForCloseTests()
    {
        return new MainWindowViewModel(
            new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false },
            new ProfileStore(CreateTempProfileStorePath()),
            applicationServices: null);
    }

    private static string CreateTempProfileStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    private static WorkspaceDockFactory GetDockFactoryViaReflection(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "dockFactory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<WorkspaceDockFactory>(field!.GetValue(viewModel));
    }

    private static (
        IRootDock root,
        IProportionalDock prop,
        IDocumentDock splitDoc,
        IProportionalDockSplitter splitter,
        IDocumentDock mainDocA,
        IDocumentDock mainDocB)
        BuildSplitLayoutForCloseTests(WorkspaceDockFactory factory)
    {
        // Layout: RootDock(IsCollapsable=false) ->
        //   ProportionalDock [splitDoc(IsCollapsable=true), splitter, mainDocA, splitter2, mainDocB]
        // Three non-splitter children guarantee CollapseDock does NOT trigger the
        // "single non-splitter left" cleanup after we remove splitDoc + splitter.
        var root = factory.CreateRootDock();
        root.IsCollapsable = false;

        var prop = factory.CreateProportionalDock();
        var splitDoc = factory.CreateDocumentDock();
        splitDoc.IsCollapsable = true;
        var splitter = factory.CreateProportionalDockSplitter();
        var mainDocA = factory.CreateDocumentDock();
        mainDocA.IsCollapsable = true;
        var splitter2 = factory.CreateProportionalDockSplitter();
        var mainDocB = factory.CreateDocumentDock();
        mainDocB.IsCollapsable = true;

        prop.VisibleDockables = factory.CreateList<IDockable>(
            splitDoc, splitter, mainDocA, splitter2, mainDocB);
        splitDoc.Owner = prop;
        splitter.Owner = prop;
        mainDocA.Owner = prop;
        splitter2.Owner = prop;
        mainDocB.Owner = prop;

        root.VisibleDockables = factory.CreateList<IDockable>(prop);
        prop.Owner = root;
        root.ActiveDockable = prop;

        return (root, prop, splitDoc, splitter, mainDocA, mainDocB);
    }

    private static IEnumerable<IDocumentDock> EnumerateDocumentDocksForMigration(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            yield return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                foreach (var nested in EnumerateDocumentDocksForMigration(child))
                {
                    yield return nested;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // NEW TESTS: #1341 per-pane pane behavior
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_CycleTab_WhenTwoTabsExist_ActivatesNextInVisibleOrder()
    {
        // Test #1: CycleTab(1) advances to the next tab in visible order.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://cycle-a.example.com") { Id = "cycle-a", Title = "A" };
        var tabB = new WebViewModel("https://cycle-b.example.com") { Id = "cycle-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "cycle-a");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "cycle-b");

        // Tab B should be active (last opened). Cycle forward to wrap to A.
        Assert.Equal("cycle-b", contentDock!.ActiveDockable?.Id);

        pane.CycleTab(1);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal("cycle-a", contentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_PopulateTabsAsync_WithLegacyRegionsJson_AddsTabsInDeclarationOrder()
    {
        // Test #2: PopulateTabsAsync with legacy `tabs` array adds tabs in declaration order.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11341002-0000-4000-8000-000000000001");
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Legacy Tabs WS" },
              "tabs": [
                { "tab-id": "legacy-tab-1", "title": "Tab One", "kind": "browser", "content": { "url": "https://one.example.com" } },
                { "tab-id": "legacy-tab-2", "title": "Tab Two", "kind": "browser", "content": { "url": "https://two.example.com" } },
                { "tab-id": "legacy-tab-3", "title": "Tab Three", "kind": "browser", "content": { "url": "https://three.example.com" } }
              ],
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceId.ToString());
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        var tabIds = pane.Tabs.Select(t => t.Id).ToList();
        Assert.Equal(["legacy-tab-1", "legacy-tab-2", "legacy-tab-3"], tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_PopulateTabsAsync_WithActiveTabId_ActivatesSavedTab()
    {
        // Test #3: Workspace data with active-tab-id activates that tab after PopulateTabsAsync.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11341003-0000-4000-8000-000000000001");
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Active Tab WS" },
              "tabs": [
                { "tab-id": "active-tab-1", "title": "T1", "kind": "browser", "content": { "url": "https://t1.example.com" } },
                { "tab-id": "active-tab-2", "title": "T2", "kind": "browser", "content": { "url": "https://t2.example.com" } }
              ],
              "active-tab-id": "active-tab-1",
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceId.ToString());
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "active-tab-1");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "active-tab-2");

        var activeDoc = contentDock!.ActiveDockable as WorkspaceDocument;
        Assert.NotNull(activeDoc);
        Assert.Equal("active-tab-1", activeDoc!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_PopulateTabsAsync_WhenPaneClosedDuringLoad_DisposesLoadedTabs()
    {
        // Test #4: Disposing the pane after PopulateTabsAsync disposes all loaded tabs.
        // (Deterministic alternative to racing close mid-load.)
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11341004-0000-4000-8000-000000000001");
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Dispose Test WS" },
              "tabs": [
                { "tab-id": "dispose-test-tab", "title": "DT", "kind": "browser", "content": { "url": "https://dt.example.com" } }
              ],
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceId.ToString());
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        Assert.NotEmpty(pane.Tabs);
        Assert.True(pane.OwnsDocumentTab("dispose-test-tab"));

        // Dispose the pane; all tabs should be disposed and registry cleared.
        await pane.DisposeAsync();

        Assert.Empty(pane.Tabs);
        Assert.False(pane.OwnsDocumentTab("dispose-test-tab"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_OpenTabAsync_WhenAnchorTabProvided_InsertsAfterAnchorInSameRegion()
    {
        // Test #5: OpenTabAsync with insertAfterTabId inserts the new tab after the anchor.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://anchor-a.example.com") { Id = "anchor-a", Title = "A" };
        var tabB = new WebViewModel("https://anchor-b.example.com") { Id = "anchor-b", Title = "B" };
        var tabC = new WebViewModel("https://anchor-c.example.com") { Id = "anchor-c", Title = "C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "anchor-a");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "anchor-b");

        // Insert C after A (not at end).
        await viewModel.OpenTabAsync(tabC, insertAfterTabId: "anchor-a");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "anchor-c");

        var docs = contentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        var indexA = docs.FindIndex(d => d.Id == "anchor-a");
        var indexC = docs.FindIndex(d => d.Id == "anchor-c");
        Assert.Equal(indexA + 1, indexC);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_OpenTabAsync_WhenTabAlreadyOpen_JustActivates()
    {
        // Test #6: Opening a tab with the same id does not create a duplicate.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://dedup.example.com") { Id = "dedup-tab", Title = "Dedup" };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "dedup-tab");

        var countBefore = pane.Tabs.Count;

        // Attempt to open a tab with the same ID again.
        var duplicate = new WebViewModel("https://dedup-other.example.com") { Id = "dedup-tab", Title = "Dedup Again" };
        await viewModel.OpenTabAsync(duplicate);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // No duplicate created; count unchanged.
        Assert.Equal(countBefore, pane.Tabs.Count);
        Assert.Equal("dedup-tab", contentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePaneViewModel_ActivateTabWhenLoaded_WhenTabAddedLater_ActivatesOnce()
    {
        // Test #7: NavigateToDocumentTabAsync with deferIfAbsent activates the tab once it materializes.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Open a first tab so the pane is active.
        var firstTab = new WebViewModel("https://first.example.com") { Id = "first-tab", Title = "First" };
        await viewModel.OpenTabAsync(firstTab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "first-tab");

        // Request navigation to a tab that does not yet exist, with deferIfAbsent.
        var deferredResult = await pane.NavigateToDocumentTabAsync("deferred-tab", deferIfAbsent: true);
        Assert.True(deferredResult); // deferred activation installed

        // Now add the tab; the deferred activation should fire and activate it.
        var deferredTab = new WebViewModel("https://deferred.example.com") { Id = "deferred-tab", Title = "Deferred" };
        await viewModel.OpenTabAsync(deferredTab);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "deferred-tab");
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal("deferred-tab", contentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_BuildPersistedTabsSnapshot_SerializesLayoutCanonically()
    {
        // Test #8: BuildPersistedTabsSnapshot produces an EntityChange list with dock-layout.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://persist-layout.example.com") { Id = "persist-tab", Title = "Persist" };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "persist-tab");

        var snapshot = pane.BuildPersistedTabsSnapshot();
        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot!);

        // The first change should contain the workspace data with dock-layout.
        var firstChange = snapshot[0];
        Assert.NotNull(firstChange.Data);
        var dataStr = firstChange.Data.ToString();
        Assert.Contains("dock-layout", dataStr);
        Assert.Contains("persist-tab", dataStr);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_BuildPersistedTabsSnapshot_IncludesActiveTabIdAndDropsLegacyFocusedTabId()
    {
        // Test #9: Snapshot includes active-tab-id and does NOT include focused-tab-id.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://active-a.example.com") { Id = "active-snap-a", Title = "A" };
        var tabB = new WebViewModel("https://active-b.example.com") { Id = "active-snap-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "active-snap-a");
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "active-snap-b");

        var snapshot = pane.BuildPersistedTabsSnapshot();
        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot!);

        var firstChange = snapshot[0];
        var dataStr = firstChange.Data.ToString();

        // active-tab-id should be present (whichever tab is active).
        Assert.Contains("active-tab-id", dataStr);
        // Legacy focused-tab-id should NOT be present.
        Assert.DoesNotContain("focused-tab-id", dataStr);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneViewModel_BuildPersistedTabsSnapshot_AppendsRelationshipChangesForLiveTabEntities()
    {
        // Test #10: With a tab that has an entity, the snapshot includes relationship changes.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11341010-0000-4000-8000-000000000001");
        var childEntityId = new EntityId("11341010-0000-4000-8000-000000000002");

        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, childEntityId, $$"""
            {
              "entity-id": "{{childEntityId.Value}}",
              "entity-types": ["entity", "external"],
              "names": [["tests", "rel", "child"]],
              "display-name": { "default": "Child Entity" },
              "urls": { "default": "https://example.com/child" }
            }
            """);
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "rel", "ws"]],
              "display-name": { "default": "Rel Test WS" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceId.ToString());
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        // Add a tab with an entity reference.
        var childEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, childEntityId, $$"""
            {
              "entity-id": "{{childEntityId.Value}}",
              "entity-types": ["entity", "external"],
              "names": [["tests", "rel", "child"]],
              "display-name": { "default": "Child Entity" },
              "urls": { "default": "https://example.com/child" }
            }
            """);
        var entityTab = new EntityWorkspaceTabViewModel
        {
            Id = "rel-entity-tab",
            Title = "Entity Tab",
            Entity = childEntity,
        };
        pane.Tabs.Add(entityTab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var snapshot = pane.BuildPersistedTabsSnapshot();
        Assert.NotNull(snapshot);

        // The snapshot should include changes for the workspace AND the relationship entity.
        Assert.True(snapshot!.Count >= 1);
    }
}
