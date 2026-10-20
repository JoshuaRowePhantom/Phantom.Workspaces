using System.IO;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class AuthorizationRequestsPopupViewModelTests
{
    [Fact]
    public async Task LiveCountsNeverAutoOpenAndOpenRowsSurviveProviderFlow()
    {
        var registry = new AuthorizationRequestRowsViewModelTests.Registry();
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var popup = new AuthorizationRequestsPopupViewModel { Rows = rows };
        Assert.Equal(TimeSpan.Zero, popup.FadeDuration);
        Assert.Equal(TimeSpan.Zero, popup.HoldDuration);
        registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()];
        registry.Notify();
        Assert.Equal(1, popup.ActionableCount);
        Assert.False(popup.IsOpen);
        popup.ToggleOpen();
        Assert.True(popup.IsOpen);
        Assert.False(popup.IsAutoClosing);
        var row = Assert.Single(rows.Rows);
        registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot(2) with
        {
            State = Llm.Interfaces.Authorization.UserAuthorizationState.Performing, CanAct = false,
        }];
        registry.Notify();
        Assert.True(popup.IsOpen);
        Assert.Equal(0, popup.ActionableCount);
        Assert.Same(row, Assert.Single(rows.Rows));
        await popup.DisposeAsync();
        Assert.False(popup.IsOpen);
        Assert.Single(rows.Rows);
    }

    [AvaloniaFact]
    public async Task RuntimeKeyUsesSharedRowsAndEscapeRestoresFocusWithoutAutoOpening()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
                profileStore: new ProfileStore(Path.Combine(directory, "profile.json")));
            var registry = new AuthorizationRequestRowsViewModelTests.Registry();
            using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
            await using var popup = new AuthorizationRequestsPopupViewModel { Rows = rows };
            var window = new MainWindow(main);
            window.AttachAuthorizationRequests(popup);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var key = window.FindControl<Button>("AuthorizationKey")!;
            Assert.Equal("Authorization requests", AutomationProperties.GetName(key));
            Assert.True(key.Focus());
            registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()];
            registry.Notify();
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.True(key.IsFocused);
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            var escape = new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent };
            key.RaiseEvent(escape);
            Dispatcher.UIThread.RunJobs();
            Assert.True(escape.Handled);
            Assert.False(popup.IsOpen);
            Assert.True(key.IsFocused);
            Assert.Single(rows.Rows);
            window.Close();
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [AvaloniaFact]
    public async Task PopupStaysOpenAcrossActualPlatformDeactivationAndLostFocusDuringProviderFlow()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".access-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
                profileStore: new ProfileStore(Path.Combine(directory, "profile.json")));
            var registry = new AuthorizationRequestRowsViewModelTests.Registry
            {
                Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()],
            };
            using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
            await using var popup = new AuthorizationRequestsPopupViewModel { Rows = rows };
            var window = new MainWindow(main);
            window.AttachAuthorizationRequests(popup);
            window.Show();
            RaisePlatformCallback(window, "Activated");
            Dispatcher.UIThread.RunJobs();
            var key = window.FindControl<Button>("AuthorizationKey")!;
            var frameworkPopup = window.FindControl<Popup>("AuthorizationPopup")!;
            Assert.False(frameworkPopup.IsLightDismissEnabled);
            Assert.True(key.Focus());
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            Assert.True(frameworkPopup.IsOpen);
            Assert.True(key.IsFocused);
            var row = Assert.Single(rows.Rows);
            registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot(2) with
            {
                State = Llm.Interfaces.Authorization.UserAuthorizationState.Performing,
                CanAct = false,
            }];
            registry.Notify();

            var deactivations = 0;
            window.Deactivated += (_, _) => deactivations++;
            RaisePlatformCallback(window, "Deactivated");
            RaisePlatformCallback(window, "LostFocus");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, deactivations);
            Assert.False(window.IsActive);
            Assert.True(popup.IsOpen);
            Assert.True(frameworkPopup.IsOpen);
            Assert.Same(row, Assert.Single(rows.Rows));

            RaisePlatformCallback(window, "Activated");
            registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot(3) with
            {
                State = Llm.Interfaces.Authorization.UserAuthorizationState.Completed,
                CanAct = false,
            }];
            registry.Notify();
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            Assert.Same(row, Assert.Single(rows.Rows));
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.True(key.IsFocused);
            key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            key.RaiseEvent(new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent });
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.True(key.IsFocused);
            window.Close();
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void RaisePlatformCallback(
        Window window,
        string callbackName)
    {
        // Exercise Avalonia's real native-notification handlers, not synthetic row state alone.
        var platform = typeof(Window).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(property => property.Name == "PlatformImpl").GetValue(window);
        Assert.NotNull(platform);
        var property = platform.GetType().GetInterfaces()
            .SelectMany(type => type.GetProperties())
            .First(property => property.Name == callbackName);
        Assert.IsType<Action>(property.GetValue(platform)).Invoke();
    }
}
