using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Tests;

public sealed partial class WorkspaceDocumentContextMenuTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeReuseReplacementAndInvalidOrigins_ClearSubscriptions()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var oldProvider = new ConfigurableProvider(Contributed("old", "Old"));
        var newProvider = new ConfigurableProvider(Contributed("new", "New"));
        var first = CreateMenuContext(vm, "native-old", oldProvider, true);
        var second = CreateMenuContext(vm, "native-new", newProvider, false);
        var item = new DocumentTabStripItem { DataContext = first.Document };
        var window = new Window { Content = item, DataContext = vm, Width = 500, Height = 200 };
        window.Show();
        try
        {
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            var oldMenu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            Assert.Equal(1, oldProvider.SubscriberCount);
            item.DataContext = second.Document;
            Assert.Equal(0, oldProvider.SubscriberCount);
            Assert.DoesNotContain(oldMenu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Old"));
            oldMenu.Close();
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item, keyboard: true);
            Assert.Equal(1, newProvider.SubscriberCount);
            Assert.Single(oldMenu.Items.OfType<MenuItem>(), value => Equals(value.Header, "New"));

            var replacementMenu = new ContextMenu { Items = { new MenuItem { Header = "Replacement default" } } };
            item.DocumentContextMenu = replacementMenu;
            Assert.Equal(0, newProvider.SubscriberCount);
            Assert.DoesNotContain(oldMenu.Items.OfType<MenuItem>(), value => Equals(value.Header, "New"));
            oldMenu.Close();
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            Assert.Single(replacementMenu.Items.OfType<MenuItem>(), value => Equals(value.Header, "New"));

            var template = item.Template;
            item.Template = new FuncControlTemplate<DocumentTabStripItem>((control, _) =>
                new Border { Name = "PART_TabBody", ContextMenu = (ContextMenu?)control.DocumentContextMenu });
            window.UpdateLayout();
            Assert.Equal(0, newProvider.SubscriberCount);
            replacementMenu.Close();
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item, keyboard: true);
            Assert.Equal(1, newProvider.SubscriberCount);
            replacementMenu.Close();
            item.Template = template;
            window.UpdateLayout();
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);

            // A selected, registered tab must not rescue a recycled invalid origin.
            item.DataContext = new object();
            Assert.Equal(0, newProvider.SubscriberCount);
            replacementMenu.Close();
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item, keyboard: true);
            Assert.Single(replacementMenu.Items);
            replacementMenu.Close();
            item.DataContext = new WorkspaceDocument((WorkspaceTabViewModel)second.Tab);
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            Assert.Single(replacementMenu.Items);
            Assert.Equal(0, newProvider.SubscriberCount);
            replacementMenu.Close();
            item.DataContext = second.Document;
            WorkspaceDocumentContextMenuBehavior.SetIsEnabled(item, false);
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            Assert.Single(replacementMenu.Items);
            Assert.Equal(0, newProvider.SubscriberCount);
            replacementMenu.Close();
            WorkspaceDocumentContextMenuBehavior.SetIsEnabled(item, true);
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            Assert.Equal(1, newProvider.SubscriberCount);
        }
        finally { window.Close(); }
        Assert.Equal(0, oldProvider.SubscriberCount);
        Assert.Equal(0, newProvider.SubscriberCount);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeProviderRefresh_UpdatesAvailabilityVisibilityAndAccessibleReason()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var availability = TabCommandAvailability.Disabled("Original reason");
        var provider = new ConfigurableProvider(new TabCommandDescriptor
        {
            Id = "mutable-native",
            Header = "Mutable native",
            Availability = _ => availability,
            ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
        });
        var context = CreateMenuContext(vm, "native-refresh", provider, true);
        var item = new DocumentTabStripItem { DataContext = context.Document };
        var window = new Window { Content = item, DataContext = vm, Width = 500, Height = 200 };
        window.Show();
        try
        {
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            MenuItem? Contribution() => menu.Items.OfType<MenuItem>().SingleOrDefault(value => Equals(value.Header, "Mutable native"));
            Assert.False(Contribution()!.IsEnabled);
            Assert.Equal("Original reason", AutomationProperties.GetHelpText(Contribution()!));
            await RefreshAsync(TabCommandAvailability.Disabled("Updated reason"),
                () => Contribution() is { } current && AutomationProperties.GetHelpText(current) == "Updated reason");
            Assert.Equal("Updated reason", ToolTip.GetTip(Contribution()!));
            await RefreshAsync(TabCommandAvailability.Enabled, () => Contribution()?.IsEnabled == true);
            await RefreshAsync(TabCommandAvailability.Hidden, () => Contribution() is null);
            Assert.DoesNotContain(menu.Items.OfType<Separator>(), value => value.Tag is not null);
            // A provider with no visible commands must remain subscribed so it can reappear.
            Assert.Equal(1, provider.SubscriberCount);
            await RefreshAsync(TabCommandAvailability.Enabled, () => Contribution()?.IsEnabled == true);
            Assert.Equal(1, provider.SubscriberCount);
            menu.Close();
            Assert.Equal(0, provider.SubscriberCount);

            async Task RefreshAsync(TabCommandAvailability value, Func<bool> expected)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(object? sender, NotifyCollectionChangedEventArgs args)
                {
                    if (expected()) { changed.TrySetResult(); }
                }
                ((INotifyCollectionChanged)menu.Items).CollectionChanged += Changed;
                try
                {
                    availability = value;
                    provider.Raise();
                    await changed.Task.WaitAsync(TestContext.Current.CancellationToken);
                }
                finally { ((INotifyCollectionChanged)menu.Items).CollectionChanged -= Changed; }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DocumentContextMenu_NativeQueuedProviderAndInvocationRefreshes_CannotReplaceNewPresentation()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProvider = new ConfigurableProvider(new TabCommandDescriptor
        {
            Id = "pending",
            Header = "Pending",
            ExecuteAsync = async (context, _) =>
            {
                Assert.Equal("queued-a", context.Tab.Id);
                started.TrySetResult();
                await release.Task;
                return TabCommandResult.Success();
            },
        });
        var secondProvider = new ConfigurableProvider(Contributed("second", "Second"));
        var first = CreateMenuContext(vm, "queued-a", firstProvider, true);
        var second = CreateMenuContext(vm, "queued-b", secondProvider, false);
        var item = new DocumentTabStripItem { DataContext = first.Document };
        var window = new Window { Content = item, DataContext = vm, Width = 500, Height = 200 };
        window.Show();
        try
        {
            await MainWindowTabCommandsIntegrationTests.OpenNativeMenuAsync(window, item);
            var menu = Assert.IsType<ContextMenu>(item.DocumentContextMenu);
            var command = Assert.IsType<AsyncRelayCommand>(menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, "Pending")).Command);
            command.Execute(null);
            await started.Task;
            firstProvider.Raise();
            menu.Close();
            item.DataContext = second.Document;
            // Open synchronously before the queued Input-priority callback can run.
            // Raise the native routed request without the headless raw-input helper's
            // implicit queue drain, keeping the old refresh queued across retargeting.
            item.RaiseEvent(new ContextRequestedEventArgs());
            Assert.True(menu.IsOpen);
            var current = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, "Second"));
            await Dispatcher.UIThread.InvokeAsync(() => Assert.Contains(current, menu.Items), DispatcherPriority.Input);
            Assert.Equal(0, firstProvider.SubscriberCount);
            Assert.Equal(1, secondProvider.SubscriberCount);
            release.TrySetResult();
            await command.LastExecutionTask!;
            await Dispatcher.UIThread.InvokeAsync(() => Assert.Contains(current, menu.Items), DispatcherPriority.Input);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), value => Equals(value.Header, "Pending"));

            secondProvider.Raise();
            menu.Close();
            item.RaiseEvent(new ContextRequestedEventArgs());
            var reopened = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, "Second"));
            Assert.NotSame(current, reopened);
            await Dispatcher.UIThread.InvokeAsync(() => Assert.Contains(reopened, menu.Items), DispatcherPriority.Input);
            menu.Close();
        }
        finally { release.TrySetResult(); window.Close(); }
    }
}
