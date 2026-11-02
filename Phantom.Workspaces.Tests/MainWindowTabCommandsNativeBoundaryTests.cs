using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed partial class MainWindowTabCommandsIntegrationTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeLabelIconAppsAndOuterHeader_KeepExactBoundaries()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        vm.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));
        var tab = CreateSessionTab("descendant");
        tab.Title = "Exact label";
        tab.TabHeader = TabHeaderViewModel.WithIcon("★", "Exact label");
        await vm.OpenTabAsync(tab);
        await vm.OpenTabAsync(CreateSessionTab("selected"));
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var item = await WaitForItemAsync(window, tab.Id);
            var label = item.GetVisualDescendants().OfType<TextBlock>().Single(value => value.Text == "Exact label");
            var icon = item.GetVisualDescendants().OfType<TextBlock>().Single(value => value.Text == "★");
            foreach (var descendant in new[] { label, icon })
            {
                object? source = null;
                void Requested(object? sender, ContextRequestedEventArgs args) => source = args.Source;
                item.AddHandler(Control.ContextRequestedEvent, Requested, RoutingStrategies.Tunnel);
                try { await OpenNativeMenuAsync(window, item, pointerOrigin: descendant); }
                finally { item.RemoveHandler(Control.ContextRequestedEvent, Requested); }
                Assert.Same(descendant, source);
                await InvokeExactAsync(item, "descendant");
            }
            await OpenNativeMenuAsync(window, item, appsKey: true);
            await InvokeExactAsync(item, "descendant");

            var outer = window.GetVisualDescendants().OfType<DocumentTabStripItem>()
                .Single(value => value.DataContext is WorkspacePaneDocument);
            await OpenNativeMenuAsync(window, outer);
            var outerMenu = Assert.IsType<ContextMenu>(outer.DocumentContextMenu);
            Assert.DoesNotContain(outerMenu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            Assert.Equal(13, outerMenu.Items.OfType<MenuItem>().Count());
            Assert.All(outerMenu.Items.OfType<MenuItem>().Where(value => value.Command is not null),
                value => Assert.Same(outer.DataContext, value.CommandParameter));
            outerMenu.Close();
        }
        finally { await MainWindowIntegrationTests.CloseWindowAsync(window); }

        async Task InvokeExactAsync(DocumentTabStripItem item, string session)
        {
            var service = new RecordingRestartService();
            vm.AgentSessionRestartService = service;
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var restart = Assert.Single(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Restart agent session…"));
            restart.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(session, (await service.LastRequest.Task).SessionId);
            menu.Close();
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativePinnedDefaults_TrackCountPoliciesOrientationLocalizationAndRetargeting()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        await vm.OpenTabAsync(CreateSessionTab("stock-a"));
        await vm.OpenTabAsync(CreateSessionTab("stock-b"));
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var item = await WaitForItemAsync(window, "tab-stock-a");
            var other = await WaitForItemAsync(window, "tab-stock-b");
            var document = Assert.IsType<WorkspaceDocument>(item.DataContext);
            var owner = Assert.IsAssignableFrom<global::Dock.Model.Controls.IDocumentDock>(document.Owner);
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var defaults = menu.Items.Cast<object>().Where(value => value is not Control control || control.Tag is null).ToArray();
            Assert.Equal(16, defaults.Length);
            Assert.Equal(3, defaults.OfType<Separator>().Count());
            var commands = defaults.OfType<MenuItem>().ToArray();
            MenuItem ByResource(string key) => commands.Single(value =>
                Equals(value.Header, menu.FindResource(key)));
            var floatItem = ByResource("DocumentTabStripItemFloatString");
            var floatAll = ByResource("DocumentTabStripItemFloatAllString");
            var close = ByResource("DocumentTabStripItemCloseString");
            var closeOthers = ByResource("DocumentTabStripItemCloseOtherTabsString");
            var closeAll = ByResource("DocumentTabStripItemCloseAllTabsString");
            var left = ByResource("DocumentTabStripItemCloseTabsLeftString");
            var right = ByResource("DocumentTabStripItemCloseTabsRightString");
            var above = ByResource("DocumentTabStripItemCloseTabsAboveString");
            var below = ByResource("DocumentTabStripItemCloseTabsBelowString");
            var horizontal = ByResource("DocumentTabStripItemNewHorizontalDockString");
            var vertical = ByResource("DocumentTabStripItemNewVerticalDockString");
            var floatSeparator = defaults.OfType<Separator>().ElementAt(0);
            var closeSeparator = defaults.OfType<Separator>().ElementAt(1);
            var unconditionalSeparator = defaults.OfType<Separator>().ElementAt(2);
            var originalPolicy = owner.DockCapabilityPolicy;
            var originalOverrides = document.DockCapabilityOverrides;
            var originalCloseLast = owner.CanCloseLastDockable;
            try
            {
                AssertVisibility(true, floatItem, floatAll, close, closeOthers, closeAll, floatSeparator, closeSeparator);
                owner.CanCloseLastDockable = false;
                AssertVisibility(true, floatItem, floatAll, close, closeOthers, closeAll, floatSeparator, closeSeparator);
                Assert.True(unconditionalSeparator.IsVisible);

                owner.DockCapabilityPolicy = new DockCapabilityPolicy { CanFloat = false, CanClose = false, CanDrag = false, CanDrop = false };
                AssertVisibility(false, floatItem, floatAll, close, closeOthers, closeAll, left, right, horizontal, vertical, floatSeparator, closeSeparator);
                document.DockCapabilityOverrides = new DockCapabilityOverrides { CanFloat = true, CanClose = true, CanDrag = true, CanDrop = true };
                AssertVisibility(true, floatItem, floatAll, close, closeOthers, closeAll, left, right, horizontal, vertical, floatSeparator, closeSeparator);
                document.DockCapabilityOverrides = new DockCapabilityOverrides { CanDrop = false };
                owner.DockCapabilityPolicy = new DockCapabilityPolicy { CanFloat = true, CanClose = true, CanDrag = true, CanDrop = true };
                AssertVisibility(false, horizontal, vertical);
                document.DockCapabilityOverrides = new DockCapabilityOverrides { CanDrag = false };
                AssertVisibility(false, horizontal, vertical);
                document.DockCapabilityOverrides = null;
                AssertVisibility(true, horizontal, vertical);

                const string localizedKey = "DocumentTabStripItemCloseString";
                var originalHeader = close.Header;
                menu.Resources[localizedKey] = "Localized close";
                Assert.Equal("Localized close", close.Header);
                menu.Resources.Remove(localizedKey);
                Assert.Equal(originalHeader, close.Header);
            }
            finally
            {
                owner.CanCloseLastDockable = originalCloseLast;
                owner.DockCapabilityPolicy = originalPolicy;
                document.DockCapabilityOverrides = originalOverrides;
                menu.Close();
            }
            foreach (var target in new[] { item, other, item })
            {
                await OpenNativeMenuAsync(window, target, appsKey: true);
                var targetMenu = Assert.IsType<ContextMenu>(target.DocumentContextMenu);
                if (ReferenceEquals(target, item))
                    Assert.Equal(defaults, targetMenu.Items.Cast<object>().Where(value => value is not Control control || control.Tag is null));
                AssertStockTargets(targetMenu, Assert.IsType<WorkspaceDocument>(target.DataContext));
                targetMenu.Close();
            }
            WorkspaceDocumentContextMenuBehavior.SetIsEnabled(item, false);
            item.DataContext = other.DataContext;
            document.CanClose = false;
            WorkspaceDocumentContextMenuBehavior.SetIsEnabled(item, true);
            try
            {
                await OpenNativeMenuAsync(window, item, appsKey: true);
                AssertStockTargets(menu, Assert.IsType<WorkspaceDocument>(other.DataContext));
                Assert.True(left.IsVisible);
                Assert.True(right.IsVisible);
                menu.Close();
            }
            finally
            {
                document.CanClose = true;
                item.DataContext = document;
            }
            foreach (var layout in new[] { DocumentTabLayout.Top, DocumentTabLayout.Left, DocumentTabLayout.Right, DocumentTabLayout.Top })
            {
                owner.TabsLayout = layout;
                var current = await WaitForItemAsync(window, "tab-stock-a");
                Assert.Equal(layout, current.GetVisualAncestors().OfType<DocumentControl>().First().TabsLayout);
                await OpenNativeMenuAsync(window, current, keyboard: true);
                var currentMenu = Assert.IsType<ContextMenu>(current.DocumentContextMenu);
                MenuItem Direction(string key) => currentMenu.Items.OfType<MenuItem>()
                    .Single(value => Equals(value.Header, currentMenu.FindResource(key)));
                AssertVisibility(layout == DocumentTabLayout.Top,
                    Direction("DocumentTabStripItemCloseTabsLeftString"), Direction("DocumentTabStripItemCloseTabsRightString"));
                AssertVisibility(layout != DocumentTabLayout.Top,
                    Direction("DocumentTabStripItemCloseTabsAboveString"), Direction("DocumentTabStripItemCloseTabsBelowString"));
                AssertStockTargets(currentMenu, document);
                currentMenu.Close();
            }
        }
        finally { await MainWindowIntegrationTests.CloseWindowAsync(window); }

        static void AssertVisibility(bool expected, params Control[] controls)
            => Assert.All(controls, value => Assert.Equal(expected, value.IsVisible));
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeSingleTab_CountAndLastDockableBindingsRemainLive()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        await vm.OpenTabAsync(CreateSessionTab("last-tab"));
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var item = await WaitForItemAsync(window, "tab-last-tab");
            var document = Assert.IsType<WorkspaceDocument>(item.DataContext);
            var owner = Assert.IsAssignableFrom<global::Dock.Model.Controls.IDocumentDock>(document.Owner);
            Assert.Single(owner.VisibleDockables!);
            owner.CanCloseLastDockable = false;
            await OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var defaults = menu.Items.OfType<MenuItem>().Where(value => value.Tag is null).ToArray();
            var removable = defaults.Take(9).ToArray();
            var separators = menu.Items.OfType<Separator>().Where(value => value.Tag is null).ToArray();
            Assert.All(removable, value => Assert.False(value.IsVisible));
            Assert.False(separators[0].IsVisible);
            Assert.False(separators[1].IsVisible);
            Assert.True(separators[2].IsVisible);
            owner.CanCloseLastDockable = true;
            Assert.All(removable.Where((_, index) => index != 6 && index != 8), value => Assert.True(value.IsVisible));
            Assert.True(separators[0].IsVisible);
            Assert.True(separators[1].IsVisible);
            owner.CanCloseLastDockable = false;
            Assert.All(removable, value => Assert.False(value.IsVisible));
            AssertStockTargets(menu, document);
            menu.Close();
        }
        finally { await MainWindowIntegrationTests.CloseWindowAsync(window); }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeIndependentHosts_DoNotLeakControlsOrTargets()
    {
        await using var firstVm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await using var secondVm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await firstVm.InitializeAsync();
        await secondVm.InitializeAsync();
        var firstService = new RecordingRestartService();
        var secondService = new RecordingRestartService();
        firstVm.AgentSessionRestartService = firstService;
        secondVm.AgentSessionRestartService = secondService;
        firstVm.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));
        secondVm.AgentSessionRestartConsentProvider = new DelegateConsentProvider((_, _) => Task.FromResult(true));
        await firstVm.OpenTabAsync(CreateSessionTab("host-a"));
        await secondVm.OpenTabAsync(CreateSessionTab("host-b"));
        var firstWindow = new MainWindow(firstVm);
        var secondWindow = new MainWindow(secondVm);
        firstWindow.Show();
        secondWindow.Show();
        try
        {
            var first = await WaitForItemAsync(firstWindow, "tab-host-a");
            var second = await WaitForItemAsync(secondWindow, "tab-host-b");
            await OpenNativeMenuAsync(firstWindow, first, appsKey: true);
            var firstMenu = Assert.IsType<ContextMenu>(first.DocumentContextMenu);
            AssertStockTargets(firstMenu, Assert.IsType<WorkspaceDocument>(first.DataContext));
            var firstItems = firstMenu.Items.OfType<MenuItem>().ToArray();
            firstItems.Single(value => Equals(value.Header, "Restart agent session…"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("host-a", (await firstService.LastRequest.Task).SessionId);
            firstMenu.Close();
            await OpenNativeMenuAsync(secondWindow, second, keyboard: true);
            var secondMenu = Assert.IsType<ContextMenu>(second.DocumentContextMenu);
            AssertStockTargets(secondMenu, Assert.IsType<WorkspaceDocument>(second.DataContext));
            Assert.NotSame(firstMenu, secondMenu);
            Assert.All(secondMenu.Items.OfType<MenuItem>(), value => Assert.DoesNotContain(value, firstItems));
            secondMenu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, "Restart agent session…"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("host-b", (await secondService.LastRequest.Task).SessionId);
            secondMenu.Close();
            second.DocumentContextMenu = firstMenu;
            await OpenNativeMenuAsync(secondWindow, second, appsKey: true);
            var replacementMenu = Assert.IsType<ContextMenu>(second.DocumentContextMenu);
            Assert.NotSame(firstMenu, replacementMenu);
            Assert.NotSame(secondMenu, replacementMenu);
            AssertStockTargets(replacementMenu, Assert.IsType<WorkspaceDocument>(second.DataContext));
            replacementMenu.Close();
            await OpenNativeMenuAsync(firstWindow, first);
            AssertStockTargets(firstMenu, Assert.IsType<WorkspaceDocument>(first.DataContext));
            Assert.Equal(firstItems.Where(value => value.Tag is null), firstMenu.Items.OfType<MenuItem>().Where(value => value.Tag is null));
            firstMenu.Close();
        }
        finally
        {
            await MainWindowIntegrationTests.CloseWindowAsync(firstWindow);
            await MainWindowIntegrationTests.CloseWindowAsync(secondWindow);
        }
    }

    private static void AssertStockTargets(ContextMenu menu, WorkspaceDocument document)
    {
        var defaults = menu.Items.OfType<MenuItem>().Where(value => value.Tag is null).ToArray();
        Assert.Equal(13, defaults.Length);
        Assert.All(defaults.Where(value => value.Command is not null),
            value => { Assert.Same(document, value.CommandParameter); Assert.True(value.Command!.CanExecute(document)); });
        var nested = defaults.SelectMany(value => value.Items.OfType<MenuItem>()).ToArray();
        Assert.Equal(5, nested.Length);
        Assert.All(nested, value => { Assert.NotNull(value.Command); Assert.Same(document.Owner, value.CommandParameter); });
    }
}
