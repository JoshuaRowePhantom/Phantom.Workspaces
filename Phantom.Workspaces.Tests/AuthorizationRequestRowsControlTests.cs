using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Interfaces.Access;
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

    [AvaloniaFact]
    public void LoadingToRuntimePreservesScrollSelectionsAndExplicitKeyboardFocusIntent()
    {
        var snapshots = Enumerable.Range(0, 30).Select(_ =>
        {
            var snapshot = AuthorizationRequestRowsViewModelTests.Snapshot();
            return snapshot with
            {
                Identifier = new() { Value = Guid.NewGuid() },
                MemoryOptions =
                [
                    snapshot.MemoryOptions[0],
                    snapshot.MemoryOptions[0] with
                    {
                        Identifier = new() { Value = Guid.NewGuid() },
                        Scope = ReleaseMemoryScopeKind.AllUses,
                        SafeDisplayName = "All uses",
                    },
                ],
                SourceOptions =
                [
                    snapshot.SourceOptions[0],
                    snapshot.SourceOptions[0] with
                    {
                        Identifier = new() { Digest = new string('b', 64) },
                        Usability = CredentialSourceUsability.AcquisitionRequired,
                        SafeDisplayName = "Provider",
                    },
                ],
            };
        }).ToArray();
        var registry = new AuthorizationRequestRowsViewModelTests.Registry { Snapshot = snapshots };
        using var rows = new AuthorizationRequestRowsViewModel { Dispatch = action => action(), Registry = registry };
        var loading = new LoadingWindow(new LoadingWindowViewModel { AuthorizationRows = rows });
        loading.Show();
        Dispatcher.UIThread.RunJobs();
        var loadingControl = Assert.Single(loading.GetVisualDescendants().OfType<AuthorizationRequestRowsControl>());
        var selected = rows.Rows[9];
        selected.SelectedMemoryOption = selected.MemoryOptions[1];
        selected.SelectedSourceOption = selected.SourceOptions[1];
        selected.IsDetailsExpanded = true;
        var loadingSource = FindTarget(loadingControl, selected, AuthorizationRowFocusTarget.Source);
        Assert.True(loadingSource.Focus());
        Dispatcher.UIThread.RunJobs();
        var loadingScroller = loadingControl.FindControl<ScrollViewer>("RowsScroller")!;
        loadingScroller.Offset = new Vector(0, 180);
        Dispatcher.UIThread.RunJobs();
        var savedOffset = loadingScroller.Offset;
        Assert.True(savedOffset.Y > 0);
        Assert.Equal(savedOffset, rows.Presentation.ScrollOffset);
        Assert.Equal(selected.Identifier, rows.Presentation.FocusedRequestIdentifier);
        Assert.Equal(AuthorizationRowFocusTarget.Source, rows.Presentation.FocusTarget);
        loading.Close();
        Dispatcher.UIThread.RunJobs();

        rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        var sentinel = new TextBox();
        var runtimeControl = new AuthorizationRequestRowsControl { DataContext = rows, Height = 260 };
        var runtime = new Window
        {
            Width = 900,
            Height = 400,
            Content = new StackPanel { Children = { sentinel, runtimeControl } },
        };
        runtime.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(sentinel.Focus());
        Assert.Same(rows, runtimeControl.DataContext);
        Assert.Same(selected, rows.Rows[9]);
        Assert.Equal(selected.MemoryOptions[1].Identifier, selected.SelectedMemory);
        Assert.Equal(selected.SourceOptions[1].Identifier, selected.SelectedSource);
        Assert.True(selected.IsDetailsExpanded);
        Assert.Equal(savedOffset, runtimeControl.FindControl<ScrollViewer>("RowsScroller")!.Offset);
        Assert.True(sentinel.IsFocused);

        PressTab(runtime);
        var runtimeSource = FindTarget(runtimeControl, selected, AuthorizationRowFocusTarget.Source);
        Assert.True(runtimeSource.IsKeyboardFocusWithin);
        PressTab(runtime);
        Assert.True(FindTarget(runtimeControl, selected, AuthorizationRowFocusTarget.PrimaryAction).IsFocused);
        Assert.Equal(AuthorizationRowFocusTarget.PrimaryAction, rows.Presentation.FocusTarget);
        var focused = runtime.FocusManager!.GetFocusedElement();
        var runtimeOffset = runtimeControl.FindControl<ScrollViewer>("RowsScroller")!.Offset;
        registry.Snapshot = snapshots.Append(AuthorizationRequestRowsViewModelTests.Snapshot() with
        {
            Identifier = new() { Value = Guid.NewGuid() },
        }).ToArray();
        registry.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(focused, runtime.FocusManager.GetFocusedElement());
        Assert.Equal(runtimeOffset, runtimeControl.FindControl<ScrollViewer>("RowsScroller")!.Offset);
        runtime.Close();
        rows.Dispose();
        Assert.Equal(default, rows.Presentation.ScrollOffset);
        Assert.Null(rows.Presentation.FocusedRequestIdentifier);
        Assert.Null(rows.Presentation.FocusTarget);
    }

    private static Control FindTarget(
        AuthorizationRequestRowsControl control,
        AuthorizationRequestRowViewModel row,
        AuthorizationRowFocusTarget target) =>
        control.GetVisualDescendants().OfType<Control>().Single(element =>
            ReferenceEquals(element.DataContext, row) && element.Tag is AuthorizationRowFocusTarget actual && actual == target);

    private static void PressTab(Window window)
    {
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Dispatcher.UIThread.RunJobs();
    }
}
