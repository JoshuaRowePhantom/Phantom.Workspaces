using Avalonia.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Agent.Gui;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Phantom.Workspaces.Tests;

public sealed partial class MainWindowTabCommandsIntegrationTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RestartSession_DefaultProductionCoordinator_RendersMenuAndReplacesTheActualOwnerInPlace(bool failStartup, bool failFinalPersistence)
    {
        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        await using var environment = new DataAccessAgentSessionRuntimeHostFactoryTests.RestartEnvironment(
            foregroundScheduler: foregroundScheduler);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        Assert.IsType<AgentSessionRestartCoordinator>(viewModel.AgentSessionRestartService);
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));
        var viewer = await environment.AcquireAsync();
        var original = viewer.AgentChat;
        var logger = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "tab-production-restart",
            Title = "Production",
            Entity = CreateWorkspaceEntity("production-restart"),
            AgentSessionId = "session",
        };
        tab.SetLease(viewer);
        tab.SetReady(new AgentViewModel(original, "production", "", logger, foregroundScheduler), logger);
        await viewModel.OpenTabAsync(tab);
        var peerViewer = await environment.AcquireAsync();
        var peerLogger = new ObservableLoggerFactory();
        var peerTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "tab-production-peer", Title = "Peer", AgentSessionId = "session",
            Entity = CreateWorkspaceEntity("production-peer"),
        };
        peerTab.SetLease(peerViewer);
        peerTab.SetReady(new AgentViewModel(original, "peer", "", peerLogger, foregroundScheduler), peerLogger);
        await viewModel.OpenTabAsync(peerTab);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(tab.IsRestartInProgress) && !tab.IsRestartInProgress)
                completed.TrySetResult();
        };
        environment.FailStartup = failStartup;
        environment.FailFinalPersistence = failFinalPersistence;
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var strip = await WaitForContentTabStripAsync(window);
            var item = strip.GetVisualDescendants().OfType<DocumentTabStripItem>()
                .Single(value => value.DataContext is WorkspaceDocument { Id: "tab-production-restart" });
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            Assert.True(restart.IsEnabled);
            restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (failFinalPersistence)
            {
                Assert.Contains("blocked", tab.RestartError);
                menu.Close();
                await OpenNativeMenuAsync(window, item);
                restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
                Assert.False(restart.IsEnabled);
                Assert.True(TabCommandContext.TryCreateForKeyboardOrigin(viewModel, peerTab, out var blockedContext));
                Assert.False(viewModel.AgentSessionRestartService.GetAvailability(peerTab, blockedContext!).IsAvailable);
                Assert.Null(viewModel.AgentSessionRestartService.PrepareRestart(peerTab, blockedContext!));
                Assert.Empty(environment.Table.RunningSessions);
                return;
            }
            if (failStartup)
            {
                Assert.Contains("Retry", tab.RestartError);
                var pending = Assert.IsType<Phantom.Workspaces.Llm.Remote.AgentSessionOwnerRestartRequest>(
                    viewer.RestartCommandState.Pending);
                Assert.Null(viewer.RestartIdentityProvider!());
                Assert.True(TabCommandContext.TryCreateForKeyboardOrigin(viewModel, peerTab, out var peerContext));
                Assert.True(viewModel.AgentSessionRestartService.GetAvailability(peerTab, peerContext!).IsAvailable);
                Assert.Equal(pending.CommandId,
                    viewModel.AgentSessionRestartService.PrepareRestart(peerTab, peerContext!)!.CommandId);
                completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                environment.FailStartup = false;
                menu.Close();
                await OpenNativeMenuAsync(window, item);
                restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
                Assert.True(restart.IsEnabled);
                restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(Phantom.Workspaces.Llm.Remote.AgentSessionOwnerRestartState.Succeeded,
                    (await viewer.RestartStatusAsync!(pending, TestContext.Current.CancellationToken))!.State);
            }
            Assert.Null(tab.RestartError);
            Assert.NotSame(original, tab.Lease!.AgentChat);
            Assert.True(Assert.IsType<AgentChat>(original).IsTerminal);
            Assert.Equal(AgentTabState.Ready, tab.State);
            Assert.Single(environment.Table.RunningSessions);
            Assert.Same(tab.Lease.AgentChat, peerTab.Lease!.AgentChat);
            Assert.True(TabCommandContext.TryCreateForKeyboardOrigin(viewModel, peerTab, out var nextContext));
            var next = viewModel.AgentSessionRestartService.PrepareRestart(peerTab, nextContext!)!;
            Assert.Equal(peerTab.Lease.RestartIdentityProvider!()!.RuntimeEpoch.Value, next.RuntimeEpoch);
            Assert.Null(peerTab.Lease.RestartCommandState.Pending);
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task RestartSession_ContextMenuTargetsInactiveTab_RestartsClickedSession()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var restartService = new RecordingRestartService();
        viewModel.AgentSessionRestartService = restartService;
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));

        var firstTab = CreateSessionTab("session-a");
        var secondTab = CreateSessionTab("session-b");
        await viewModel.OpenTabAsync(firstTab);
        await viewModel.OpenTabAsync(secondTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var contentTabStrip = await WaitForContentTabStripAsync(window);
            var inactiveTabItem = contentTabStrip
                .GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .Single(item => item.DataContext is WorkspaceDocument { Id: "tab-session-a" });
            var menu = Assert.IsType<ContextMenu>(inactiveTabItem.DocumentContextMenu);
            await OpenNativeMenuAsync(window, inactiveTabItem);
            var restartItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Restart agent session…"));
            Assert.NotNull(restartItem.Command);

            restartItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            var request = await restartService.LastRequest.Task;

            Assert.Equal(firstTab.AgentSessionId, request.SessionId);
            Assert.NotEqual(secondTab.AgentSessionId, request.SessionId);
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task RestartSession_RepeatedMenuOpen_PreservesDefaultsWithoutDuplicates()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        viewModel.AgentSessionRestartService = new RecordingRestartService();
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));

        await viewModel.OpenTabAsync(CreateSessionTab("session-a"));
        await viewModel.OpenTabAsync(CreateSessionTab("session-b"));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var contentTabStrip = await WaitForContentTabStripAsync(window);
            var tabItem = contentTabStrip
                .GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .Single(item => item.DataContext is WorkspaceDocument { Id: "tab-session-a" });
            var menu = Assert.IsType<ContextMenu>(tabItem.DocumentContextMenu);
            await OpenNativeMenuAsync(window, tabItem);
            var defaultHeaders = menu.Items.OfType<MenuItem>()
                .Where(item => !Equals(item.Header, "Restart agent session…"))
                .Select(item => item.Header)
                .ToList();
            Assert.NotEmpty(defaultHeaders);

            menu.Close();
            await OpenNativeMenuAsync(window, tabItem);
            menu.Close();
            await OpenNativeMenuAsync(window, tabItem);

            Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Restart agent session…"));
            var afterHeaders = menu.Items.OfType<MenuItem>()
                .Where(item => !Equals(item.Header, "Restart agent session…"))
                .Select(item => item.Header)
                .ToList();
            Assert.Equal(defaultHeaders, afterHeaders);
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_RenderedSplitFloatAndRestore_TargetsExactContent()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));

        await viewModel.OpenTabAsync(CreateSessionTab("session-a"));
        await viewModel.OpenTabAsync(CreateSessionTab("session-b"));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var contentTabStrip = await WaitForContentTabStripAsync(window);
            var renderedItems = contentTabStrip
                .GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .Where(item => item.DataContext is WorkspaceDocument)
                .ToList();
            Assert.Equal(2, renderedItems.Count);

            // Each rendered content container targets its own exact document, not the active tab.
            foreach (var item in renderedItems)
            {
                var document = (WorkspaceDocument)item.DataContext!;
                var expectedSessionId = document.Id["tab-".Length..];
                var restartService = new RecordingRestartService();
                viewModel.AgentSessionRestartService = restartService;

                var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
                await OpenNativeMenuAsync(window, item);
                var restartItem = Assert.Single(
                    menu.Items.OfType<MenuItem>(),
                    menuItem => Equals(menuItem.Header, "Restart agent session…"));
                Assert.NotNull(restartItem.Command);

                restartItem.Command!.Execute(null);
                var request = await restartService.LastRequest.Task;
                Assert.Equal(expectedSessionId, request.SessionId);

                menu.Close();
            }

            var pane = viewModel.SelectedWorkspacePane;
            var documentA = pane.GetDocumentForTab("tab-session-a")!;
            var factory = Assert.IsType<WorkspaceDockFactory>(documentA.Factory);
            factory.NewHorizontalDocumentDock(documentA);
            window.UpdateLayout();
            Assert.NotSame(pane.GetDocumentForTab("tab-session-a")!.Owner, pane.GetDocumentForTab("tab-session-b")!.Owner);
            await AssertNativeTargetAsync(window, "tab-session-a");
            await AssertNativeTargetAsync(window, "tab-session-b");

            var serialized = DockLayoutCanonicalizer.SerializeCanonical(pane.ContentLayout!, pane.Tabs.Select(tab => tab.Id).ToHashSet());
            var restored = DockLayoutCanonicalizer.Deserialize(serialized)!;
            foreach (var dock in MainWindowViewModel.EnumerateContentDocks(pane.ContentLayout!))
                dock.ItemsSource = null;
            foreach (var document in MainWindowViewModel.EnumerateAllDocuments(restored))
            {
                var tab = pane.Tabs.Single(tab => tab.Id == document.Id);
                document.Initialize(tab);
                pane.RegisterDocument(document.Id, document);
            }
            pane.ContentLayout = restored;
            factory.InitLayout(restored);
            factory.DockState.Restore(restored);
            foreach (var dock in MainWindowViewModel.EnumerateContentDocks(restored))
                factory.WireContentDock(dock, pane, ownsTabs: false);
            window.UpdateLayout();
            await AssertNativeTargetAsync(window, "tab-session-a");
            await AssertNativeTargetAsync(window, "tab-session-b");

            var floatingOpened = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((host, _) =>
            {
                if (!ReferenceEquals(host, window)) { floatingOpened.TrySetResult(host); }
            });
            var floatingDocument = pane.GetDocumentForTab("tab-session-a")!;
            ((global::Dock.Model.Controls.IDocumentDock)floatingDocument.Owner!).CanCloseLastDockable = true;
            factory.FloatDockable(floatingDocument);
            var floating = await floatingOpened.Task.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                await AssertNativeTargetAsync(floating, "tab-session-a");
                Assert.Contains(pane.Tabs, tab => tab.Id == "tab-session-a");
            }
            finally { floating.Close(); }

            async Task AssertNativeTargetAsync(Window host, string tabId)
            {
                var item = await WaitForItemAsync(host, tabId);
                var service = new RecordingRestartService();
                viewModel.AgentSessionRestartService = service;
                await OpenNativeMenuAsync(host, item, keyboard: true);
                var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
                var restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
                AssertStockTargets(menu, Assert.IsType<WorkspaceDocument>(item.DataContext));
                restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(tabId["tab-".Length..], (await service.LastRequest.Task).SessionId);
                menu.Close();
            }
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_KeyboardOpenAndConcurrentMenus_PreserveDefaultsAndTargets()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        viewModel.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));

        await viewModel.OpenTabAsync(CreateSessionTab("session-a"));
        await viewModel.OpenTabAsync(CreateSessionTab("session-b"));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var contentTabStrip = await WaitForContentTabStripAsync(window);
            var renderedItems = contentTabStrip
                .GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .Where(item => item.DataContext is WorkspaceDocument)
                .ToList();
            Assert.Equal(2, renderedItems.Count);

            var itemA = renderedItems.Single(item => item.DataContext is WorkspaceDocument { Id: "tab-session-a" });
            var itemB = renderedItems.Single(item => item.DataContext is WorkspaceDocument { Id: "tab-session-b" });

            // Revisit A/B/A through each isolated native menu without losing defaults
            // or accumulating contributions from a previous presentation.
            var menu = Assert.IsType<ContextMenu>(itemA.DocumentContextMenu);
            await OpenNativeMenuAsync(window, itemA, keyboard: true);
            var defaultHeaders = menu.Items.OfType<MenuItem>()
                .Where(item => !Equals(item.Header, "Restart agent session…")).Select(item => item.Header).ToList();
            menu.Close();

            await AssertRetargetsAsync(menu, itemA, "session-a", defaultHeaders);
            await AssertRetargetsAsync(menu, itemB, "session-b", defaultHeaders);
            await AssertRetargetsAsync(menu, itemA, "session-a", defaultHeaders);
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(window);
        }

        async Task AssertRetargetsAsync(
            ContextMenu menu,
            DocumentTabStripItem item,
            string expectedSessionId,
            IReadOnlyList<object?> defaultHeaders)
        {
            menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            await OpenNativeMenuAsync(window, item, keyboard: true);

            var restartItem = Assert.Single(
                menu.Items.OfType<MenuItem>(),
                menuItem => Equals(menuItem.Header, "Restart agent session…"));
            var preserved = menu.Items.OfType<MenuItem>()
                .Where(menuItem => !Equals(menuItem.Header, "Restart agent session…"))
                .Select(menuItem => menuItem.Header)
                .ToList();
            Assert.Equal(defaultHeaders, preserved);

            var restartService = new RecordingRestartService();
            viewModel.AgentSessionRestartService = restartService;
            restartItem.Command!.Execute(null);
            Assert.Equal(expectedSessionId, (await restartService.LastRequest.Task).SessionId);

            menu.Close();
        }
    }

    private static async Task<DocumentTabStripItem> WaitForItemAsync(Window window, string tabId)
    {
        var completed = new TaskCompletionSource<DocumentTabStripItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated(object? sender, EventArgs arguments)
        {
            var item = window.GetVisualDescendants().OfType<DocumentTabStripItem>().FirstOrDefault(item =>
                item.DataContext is WorkspaceDocument document && document.Id == tabId);
            if (item is not null) { completed.TrySetResult(item); }
        }
        window.LayoutUpdated += Updated;
        try
        {
            window.UpdateLayout();
            Updated(null, EventArgs.Empty);
            return await completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally { window.LayoutUpdated -= Updated; }
    }

    internal static async Task OpenNativeMenuAsync(Window window, DocumentTabStripItem item, bool keyboard = false,
        Control? pointerOrigin = null, bool appsKey = false)
    {
        window.UpdateLayout();
        var body = item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_TabBody");
        var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
        Assert.Null(menu.PlacementTarget);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(object? sender, EventArgs arguments) => opened.TrySetResult();
        menu.Opened += Opened;
        try
        {
            if (keyboard || appsKey)
            {
                Assert.True(item.Focus());
                window.KeyPress(appsKey ? Key.Apps : Key.F10, appsKey ? RawInputModifiers.None : RawInputModifiers.Shift,
                    appsKey ? PhysicalKey.ContextMenu : PhysicalKey.F10, null);
                window.KeyRelease(appsKey ? Key.Apps : Key.F10, appsKey ? RawInputModifiers.None : RawInputModifiers.Shift,
                    appsKey ? PhysicalKey.ContextMenu : PhysicalKey.F10, null);
            }
            else
            {
                var target = pointerOrigin ?? body;
                var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
            }
            await opened.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(menu.IsOpen);
        }
        finally { menu.Opened -= Opened; }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeDefaults_KeepCapabilitiesParametersAndNestedActions()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(CreateSessionTab("caps-a"));
        await viewModel.OpenTabAsync(CreateSessionTab("caps-b"));
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var strip = await WaitForContentTabStripAsync(window);
            var item = strip.GetVisualDescendants().OfType<DocumentTabStripItem>()
                .Single(item => item.DataContext is WorkspaceDocument { Id: "tab-caps-a" });
            var document = Assert.IsType<WorkspaceDocument>(item.DataContext);
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var defaults = menu.Items.OfType<MenuItem>().Where(value => !Equals(value.Header, "Restart agent session…")).ToArray();
            Assert.Equal(13, defaults.Length);
            Assert.All(defaults.Where(value => value.Command is not null), value => Assert.Same(document, value.CommandParameter));
            var nested = defaults.Where(value => value.Items.Count != 0).ToArray();
            Assert.Equal(2, nested.Length);
            Assert.Equal(5, nested.Sum(value => value.Items.Count));
            Assert.All(nested.SelectMany(value => value.Items.OfType<MenuItem>()),
                value => { Assert.NotNull(value.Command); Assert.Same(document.Owner, value.CommandParameter); });
            var floating = defaults.Single(value => Equals(value.Header, "_Float"));
            var close = defaults.Single(value => Equals(value.Header, "_Close"));
            Assert.True(floating.IsVisible);
            Assert.True(close.IsVisible);
            document.CanFloat = false;
            document.CanClose = false;
            menu.UpdateLayout();
            Assert.False(floating.IsVisible);
            Assert.False(close.IsVisible);
            document.CanFloat = true;
            document.CanClose = true;
            menu.UpdateLayout();
            Assert.True(floating.IsVisible);
            Assert.True(close.IsVisible);
            Assert.All(defaults, value => Assert.Contains(value, menu.Items));
            menu.Close();
        }
        finally { await MainWindowIntegrationTests.CloseWindowAsync(window); }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task RestartSession_NativeNonAgentDocument_PreservesDefaultsWithoutRestart()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(new WebViewModel("https://native-menu.example.invalid") { Id = "browser", Title = "Browser" });
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var strip = await WaitForContentTabStripAsync(window);
            var item = Assert.Single(strip.GetVisualDescendants().OfType<DocumentTabStripItem>());
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            Assert.Contains(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "_Float"));
            Assert.Contains(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "_Close"));
            menu.Close();
        }
        finally { await MainWindowIntegrationTests.CloseWindowAsync(window); }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task RestartSession_UnavailableRuntimeNativeMenu_ShowsAccessibleDisabledExplanation()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(CreateSessionTab("unsupported"));
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var strip = await WaitForContentTabStripAsync(window);
            var item = Assert.Single(strip.GetVisualDescendants().OfType<DocumentTabStripItem>());
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            AssertStockTargets(menu, Assert.IsType<WorkspaceDocument>(item.DataContext));
            Assert.False(restart.IsEnabled);
            Assert.Equal("Restart agent session", AutomationProperties.GetName(restart));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(restart)));
            menu.Close();
        }
        finally { await MainWindowIntegrationTests.CloseWindowAsync(window); }
    }

    private static async Task<DocumentTabStrip> WaitForContentTabStripAsync(Window window)
    {
        var completion = new TaskCompletionSource<DocumentTabStrip>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? updated = null;
        updated = (_, _) =>
        {
            var strip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(candidate => candidate.DataContext is WorkspaceContentDock);
            if (strip is null)
            {
                return;
            }

            var items = strip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
            if (items.Count == 0)
            {
                return;
            }

            window.LayoutUpdated -= updated;
            completion.TrySetResult(strip);
        };

        window.LayoutUpdated += updated;
        window.UpdateLayout();
        updated(null, EventArgs.Empty);
        return await completion.Task;
    }

    private static AgentSessionWorkspaceTabViewModel CreateSessionTab(string sessionId)
    {
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = $"tab-{sessionId}",
            Title = sessionId,
            Entity = CreateWorkspaceEntity(sessionId),
            AgentSessionId = sessionId,
        };
        tab.SetLease(new RunningAgentChatLease(new AgentSessionId(sessionId), CreateChat(), () => ValueTask.CompletedTask));
        return tab;
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity(string id)
        => new(new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid().ToString("N")),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""
                {
                  "entity-id": "{{id}}",
                  "display-name": { "default": "{{id}}" },
                  "entity-types": ["entity", "workspace"]
                }
                """).RootElement.Clone(),
            Relationships = [],
        });

    private static IAgentChat CreateChat()
    {
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.HistoryPopulated).Returns(Task.CompletedTask);
        chat.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        chat.SetupGet(value => value.Modals).Returns(
            new ReadOnlyObservableCollection<AgentChatModal>(new ObservableCollection<AgentChatModal>()));
        chat.SetupGet(value => value.SubAgents).Returns(
            new ReadOnlyObservableCollection<IRunningSubAgent>(new ObservableCollection<IRunningSubAgent>()));
        var queues = new Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot
        {
            Revision = 1,
            Queues = ImmutableArray<AgentInputQueueSnapshot>.Empty,
        });
        chat.SetupGet(value => value.InputQueues).Returns(queues.Object);
        return chat.Object;
    }

    private sealed class RecordingRestartService : IAgentSessionRestartService
    {
        public Task<AgentSessionRestartResult?> GetStatusAsync(
            AgentSessionRestartRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult<AgentSessionRestartResult?>(null);

        private readonly object gate = new();

        public TaskCompletionSource<AgentSessionRestartRequest> LastRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AgentSessionRestartRequest> AllRequests { get; } = [];
        private readonly Guid epoch = Guid.NewGuid();

        public AgentSessionRestartAvailability GetAvailability(
            AgentSessionWorkspaceTabViewModel tab,
            TabCommandContext context)
            => new()
            {
                IsAvailable = true,
            };

        public AgentSessionRestartRequest? PrepareRestart(
            AgentSessionWorkspaceTabViewModel tab, TabCommandContext context)
            => new()
            {
                SessionId = tab.AgentSessionId!,
                ExpectedChat = tab.Lease!.AgentChat,
                ExpectedLease = tab.Lease,
                CommandId = Guid.NewGuid(),
                ExpectedOwnerId = "authoritative-owner-profile",
                OwnershipGeneration = 42,
                RuntimeEpoch = this.epoch,
            };

        public Task<AgentSessionRestartResult> RestartAsync(
            AgentSessionRestartRequest request,
            CancellationToken cancellationToken)
        {
            lock (this.gate)
            {
                this.AllRequests.Add(request);
            }

            this.LastRequest.TrySetResult(request);
            return Task.FromResult(new AgentSessionRestartResult
            {
                Outcome = AgentSessionRestartOutcome.Succeeded,
            });
        }
    }

    private sealed class DelegateConsentProvider(
        Func<string, CancellationToken, Task<bool>> confirmAsync) : IAgentSessionRestartConsentProvider
    {
        public Task<bool> ConfirmAsync(string sessionName, CancellationToken cancellationToken)
            => confirmAsync(sessionName, cancellationToken);
    }
}
