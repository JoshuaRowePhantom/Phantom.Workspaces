using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class AuthorizationRequestRowsControlTests
{
    [AvaloniaFact]
    public void LoadingHostBindsExactSharedRowsBelowStatus()
    {
        var registry = new AuthorizationRequestRowsViewModelTests.Registry
        {
            Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()],
        };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var window = new LoadingWindow(new LoadingWindowViewModel
        {
            AuthorizationRows = rows,
            StatusText = "Loading services.",
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var control = Assert.Single(window.GetVisualDescendants().OfType<AuthorizationRequestRowsControl>());
        Assert.Same(rows, control.DataContext);
        Assert.True(control.IsBootstrap);
        Assert.Equal(3, Grid.GetRow(control));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text == "Loading services." && Grid.GetRow(text) == 2);
        window.Close();
    }

    [AvaloniaFact]
    public void RendersLiveFourColumnGridAndDisablesPerformingActions()
    {
        var registry = new AuthorizationRequestRowsViewModelTests.Registry
        {
            Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()],
        };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var control = new AuthorizationRequestRowsControl { DataContext = rows };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(control.GetVisualDescendants().OfType<Grid>(), grid => grid.ColumnDefinitions.Count == 4);
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button => button.Content?.ToString() == "Allow");
        registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot(2) with
        {
            State = Llm.Interfaces.Authorization.UserAuthorizationState.Performing,
            CanAct = false,
        }];
        registry.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.All(control.GetVisualDescendants().OfType<Button>(), button => Assert.False(button.IsEffectivelyEnabled));
        window.Close();
    }

    [AvaloniaFact]
    public void BootstrapFocusesOnceBlocksEscapeAndKeepsPoliteAccessibleStatus()
    {
        var registry = new AuthorizationRequestRowsViewModelTests.Registry
        {
            Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()],
        };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var control = new AuthorizationRequestRowsControl { IsBootstrap = true, DataContext = rows };
        var sentinel = new TextBox();
        var panel = new StackPanel { Children = { sentinel, control } };
        var window = new Window { Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(control.GetVisualDescendants().OfType<Button>(), button => button.IsFocused);
        Assert.Contains(control.GetVisualDescendants().OfType<ComboBox>(), combo =>
            AutomationProperties.GetName(combo) == "Credential source");
        Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(), text =>
            AutomationProperties.GetLiveSetting(text).ToString() == "Polite");
        var escape = new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent };
        control.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.True(sentinel.Focus());
        registry.Snapshot =
        [
            AuthorizationRequestRowsViewModelTests.Snapshot(2),
            AuthorizationRequestRowsViewModelTests.Snapshot() with { Identifier = new() { Value = Guid.NewGuid() } },
        ];
        registry.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.True(sentinel.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BackgroundNotificationsAndDisposalMutateRowsOnlyThroughUiDispatcher()
    {
        var registry = new AuthorizationRequestRowsViewModelTests.Registry
        {
            Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot()],
        };
        var rows = new AuthorizationRequestRowsViewModel { Registry = registry };
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(rows.Rows);
        var notifications = new List<bool>();
        ((System.Collections.Specialized.INotifyCollectionChanged)rows.Rows).CollectionChanged +=
            (_, _) => notifications.Add(Dispatcher.UIThread.CheckAccess());
        await Task.Run(() =>
        {
            registry.Snapshot = [AuthorizationRequestRowsViewModelTests.Snapshot(2)];
            registry.Notify();
        }, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, row.Revision);
        await Task.Run(rows.Dispose, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(rows.Rows);
        Assert.Equal(0, registry.Subscribers);
        Assert.NotEmpty(notifications);
        Assert.All(notifications, Assert.True);
    }
}
