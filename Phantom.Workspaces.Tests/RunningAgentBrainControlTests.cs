using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentBrainControlTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_HeaderLabel_ReadsRunningAgents()
    {
        var control = new RunningAgentBrainControl();
        var window = new Window { Content = control };
        window.Show();

        try
        {
            var textBlocks = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            var header = textBlocks.FirstOrDefault(tb => tb.Text == "Running agents");
            Assert.NotNull(header);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrainControl_RowActions_AppearBelowDescription()
    {
        var row = CreateRemoteRow(terminateAsync: _ => Task.FromException(new InvalidOperationException()));
        row.TerminateCommand.Execute(null);
        await Assert.IsType<AsyncRelayCommand>(row.TerminateCommand).LastExecutionTask!;

        var (window, rowGrid) = ShowRowAndGet<Grid>(row, HasClass<Grid>("running-agent-row"));
        try
        {
            var activateButton = rowGrid.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, row.ActivateCommand));
            var error = rowGrid.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(text => text.Text == "Unable to terminate agent session.");
            var footer = rowGrid.GetVisualDescendants()
                .OfType<WrapPanel>()
                .Single(HasClass<WrapPanel>("running-agent-row-actions"));

            var activateBounds = BoundsRelativeTo(activateButton, rowGrid);
            var errorBounds = BoundsRelativeTo(error, rowGrid);
            var footerBounds = BoundsRelativeTo(footer, rowGrid);

            Assert.True(errorBounds.Top >= activateBounds.Bottom);
            Assert.True(footerBounds.Top >= errorBounds.Bottom);
            Assert.Equal(1, Grid.GetRow(error));
            Assert.Equal(2, Grid.GetRow(footer));
            Assert.Equal(rowGrid.Bounds.Width, footerBounds.Width, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_RowActions_RevealOnPointerEnterAndHideOnExitWithoutLayoutShift()
    {
        var row = CreateRemoteRow(isInterruptible: true);
        var (window, rowGrid) = ShowRowAndGet<Grid>(row, HasClass<Grid>("running-agent-row"));

        try
        {
            var actions = GetRowActions(rowGrid);
            DisableOpacityTransitions(actions);
            var originalBounds = actions.Select(action => BoundsRelativeTo(action, rowGrid)).ToArray();

            Assert.All(actions, action => Assert.Equal(0, action.Opacity));

            window.MouseMove(PointInside(rowGrid, window), RawInputModifiers.None);
            Assert.All(actions, action => Assert.Equal(1, action.Opacity));
            Assert.Equal(originalBounds, actions.Select(action => BoundsRelativeTo(action, rowGrid)).ToArray());

            window.MouseMove(new Point(window.ClientSize.Width - 1, window.ClientSize.Height - 1), RawInputModifiers.None);
            Assert.All(actions, action => Assert.Equal(0, action.Opacity));
            Assert.Equal(originalBounds, actions.Select(action => BoundsRelativeTo(action, rowGrid)).ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_RowActions_RemainVisibleForFocusWithinAndTabNavigation()
    {
        var row = CreateRemoteRow(isInterruptible: true);
        var (window, rowGrid) = ShowRowAndGet<Grid>(row, HasClass<Grid>("running-agent-row"));

        try
        {
            var actions = GetRowActions(rowGrid);
            DisableOpacityTransitions(actions);
            var activateButton = rowGrid.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, row.ActivateCommand));

            Assert.True(activateButton.Focus());
            Assert.All(actions, action => Assert.Equal(1, action.Opacity));

            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");

            var toggle = Assert.IsType<CheckBox>(actions[0]);
            Assert.True(toggle.IsFocused);
            Assert.Equal(1, toggle.Opacity);
            Assert.True(toggle.IsHitTestVisible);
            Assert.True(toggle.Bounds.Height >= 28);

            window.MouseMove(new Point(window.ClientSize.Width - 1, window.ClientSize.Height - 1), RawInputModifiers.None);
            Assert.True(toggle.IsFocused);
            Assert.All(actions, action => Assert.Equal(1, action.Opacity));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_CheckedAction_RemainsVisibleWithoutHover()
    {
        var row = CreateRemoteRow(continueInBackground: true);
        var (window, checkBox) = ShowRowAndGet<CheckBox>(
            row,
            control => control.Classes.Contains("running-agent-row-action-toggle"));

        try
        {
            checkBox.Transitions = null;
            Assert.True(checkBox.IsChecked);
            Assert.Equal(1, checkBox.Opacity);
            Assert.True(checkBox.Bounds.Width > 28);
            Assert.True(checkBox.Bounds.Height >= 28);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_DisconnectedAndTerminalActions_PreserveStateAndRevealWithRow()
    {
        var disconnected = CreateRemoteRow(
            isInterruptible: true,
            isConnected: false,
            entityName: "Disconnected session");
        var terminal = CreateRemoteRow(
            isInterruptible: true,
            isTerminal: true,
            entityName: "Terminal session");
        var (window, control) = ShowRows(disconnected, terminal);

        try
        {
            var rows = control.GetVisualDescendants()
                .OfType<Grid>()
                .Where(HasClass<Grid>("running-agent-row"))
                .ToArray();
            Assert.Equal(2, rows.Length);

            foreach (var row in rows)
            {
                var actions = GetRowActions(row);
                DisableOpacityTransitions(actions);
                Assert.All(actions, action => Assert.False(action.IsEnabled));
                Assert.All(actions, action => Assert.Equal(0, action.Opacity));

                window.MouseMove(PointInside(row, window), RawInputModifiers.None);

                Assert.All(actions, action => Assert.False(action.IsEnabled));
                Assert.All(actions, action => Assert.Equal(1, action.Opacity));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrainControl_PendingInterruption_RemainsVisibleAndDisabled()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = CreateRemoteRow(isInterruptible: true, interruptAsync: _ => completion.Task);
        row.InterruptCommand.Execute(null);

        var (window, interruptButton) = ShowRowAndGet<Button>(
            row,
            control => string.Equals(AutomationProperties.GetName(control), "Interrupt agent", StringComparison.Ordinal));
        try
        {
            interruptButton.Transitions = null;
            Assert.True(row.IsInterruptPending);
            Assert.Contains("pending", interruptButton.Classes);
            Assert.False(interruptButton.IsEnabled);
            Assert.Equal(1, interruptButton.Opacity);
        }
        finally
        {
            completion.SetResult();
            await Assert.IsType<AsyncRelayCommand>(row.InterruptCommand).LastExecutionTask!;
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrainControl_PendingTermination_RemainsVisibleAndDisablesEveryAction()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = CreateRemoteRow(isInterruptible: true, terminateAsync: _ => completion.Task);
        row.TerminateCommand.Execute(null);

        var (window, rowGrid) = ShowRowAndGet<Grid>(row, HasClass<Grid>("running-agent-row"));
        try
        {
            var actions = GetRowActions(rowGrid);
            DisableOpacityTransitions(actions);
            var terminateButton = actions
                .OfType<Button>()
                .Single(control =>
                    string.Equals(AutomationProperties.GetName(control), "Terminate agent session", StringComparison.Ordinal));

            Assert.True(row.IsTerminationPending);
            Assert.Contains("pending", terminateButton.Classes);
            Assert.All(actions, action => Assert.False(action.IsEnabled));
            Assert.Equal(1, terminateButton.Opacity);
        }
        finally
        {
            completion.SetResult();
            await Assert.IsType<AsyncRelayCommand>(row.TerminateCommand).LastExecutionTask!;
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_MultipleRows_HoverRevealDoesNotLeak()
    {
        var first = CreateRemoteRow(isInterruptible: true, entityName: "First session");
        var second = CreateRemoteRow(isInterruptible: true, entityName: "Second session");
        var (window, rows) = ShowRows(first, second);

        try
        {
            var rowGrids = rows.GetVisualDescendants()
                .OfType<Grid>()
                .Where(HasClass<Grid>("running-agent-row"))
                .ToArray();
            Assert.Equal(2, rowGrids.Length);
            var firstActions = GetRowActions(rowGrids[0]);
            var secondActions = GetRowActions(rowGrids[1]);
            DisableOpacityTransitions([.. firstActions, .. secondActions]);

            window.MouseMove(PointInside(rowGrids[0], window), RawInputModifiers.None);
            Assert.All(firstActions, action => Assert.Equal(1, action.Opacity));
            Assert.All(secondActions, action => Assert.Equal(0, action.Opacity));

            window.MouseMove(PointInside(rowGrids[1], window), RawInputModifiers.None);
            Assert.All(firstActions, action => Assert.Equal(0, action.Opacity));
            Assert.All(secondActions, action => Assert.Equal(1, action.Opacity));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RunningAgentBrainControl_RowActions_WrapAtNarrowWidthWithoutOverflow()
    {
        var row = CreateRemoteRow(isInterruptible: true);
        var (window, rowGrid) = ShowRowAndGet<Grid>(row, HasClass<Grid>("running-agent-row"));

        try
        {
            var panel = window.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("notifications-panel"));
            panel.Width = 180;
            window.UpdateLayout();

            var footer = rowGrid.GetVisualDescendants()
                .OfType<WrapPanel>()
                .Single(HasClass<WrapPanel>("running-agent-row-actions"));
            var actions = GetRowActions(rowGrid);
            var distinctLines = actions.Select(action => action.Bounds.Y).Distinct().Count();

            Assert.True(distinctLines > 1);
            Assert.All(actions, action => Assert.True(action.Bounds.Right <= footer.Bounds.Width + 0.5));
            Assert.True(footer.Bounds.Top >= rowGrid.GetVisualDescendants().OfType<Button>().First().Bounds.Bottom);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void ContinueInBackgroundCheckbox_Accessibility_UsesRequiredLabelAndTooltip()
    {
        var axaml = File.ReadAllText(FindAxaml());

        Assert.Contains("Content=\"Continue in background\"", axaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Continue in background\"", axaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"Keep this agent running when the last viewer disconnects.\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ContinueInBackground, Mode=OneWay}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsBackgroundOptionEnabled}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SetContinueInBackgroundCommand}\"", axaml, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void ContinueInBackgroundCheckbox_RemoteRow_BindsCheckedEnabledAndCommand()
    {
        var row = CreateRemoteRow(continueInBackground: true);
        var (window, checkBox) = ShowRowAndGet<CheckBox>(row, control =>
            string.Equals(control.Content?.ToString(), "Continue in background", StringComparison.Ordinal));

        try
        {
            Assert.Same(row, checkBox.DataContext);
            Assert.True(row.IsRemote);
            Assert.True(checkBox.IsChecked);
            Assert.True(checkBox.IsEnabled);
            Assert.Same(row.SetContinueInBackgroundCommand, checkBox.Command);
            Assert.IsType<bool>(checkBox.CommandParameter);
            Assert.True((bool)checkBox.CommandParameter!);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ContinueInBackgroundCheckbox_KeyboardSpace_DoesNotActivateRow()
    {
        var activationCount = 0;
        var requestedValues = new List<bool>();
        var row = CreateRemoteRow(
            activate: () => activationCount++,
            setContinueInBackgroundAsync: (value, _) =>
            {
                requestedValues.Add(value);
                return Task.CompletedTask;
            });
        var (window, checkBox) = ShowRowAndGet<CheckBox>(row, control =>
            string.Equals(control.Content?.ToString(), "Continue in background", StringComparison.Ordinal));

        try
        {
            Assert.True(checkBox.Focus());
            checkBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Space,
                Source = checkBox,
            });
            checkBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = Key.Space,
                Source = checkBox,
            });
            Dispatcher.UIThread.RunJobs();

            var command = Assert.IsType<AsyncRelayCommand>(row.SetContinueInBackgroundCommand);
            await command.LastExecutionTask!;
            Assert.Equal([true], requestedValues);
            Assert.Equal(0, activationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InterruptButton_ActiveRun_IsRedRightAlignedAndAccessible()
    {
        var interruptCount = 0;
        var row = CreateRemoteRow(
            isInterruptible: true,
            interruptAsync: _ =>
            {
                interruptCount++;
                return Task.CompletedTask;
            });
        var (window, interruptButton) = ShowRowAndGet<Button>(row, control =>
            string.Equals(AutomationProperties.GetName(control), "Interrupt agent", StringComparison.Ordinal));

        try
        {
            Assert.True(interruptButton.IsEnabled);
            Assert.Equal(HorizontalAlignment.Right, interruptButton.HorizontalAlignment);
            Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(interruptButton.Background).Color);
            Assert.Equal("Interrupt agent", ToolTip.GetTip(interruptButton));
            Assert.Same(row.InterruptCommand, interruptButton.Command);

            interruptButton.Command!.Execute(interruptButton.CommandParameter);
            var command = Assert.IsType<AsyncRelayCommand>(row.InterruptCommand);
            await command.LastExecutionTask!;
            Assert.Equal(1, interruptCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TerminateButton_ConnectedRow_IsDistinctAccessibleAndSendsExplicitTerminate()
    {
        var interruptCount = 0;
        var terminateCount = 0;
        var row = CreateRemoteRow(
            isInterruptible: true,
            interruptAsync: _ =>
            {
                interruptCount++;
                return Task.CompletedTask;
            },
            terminateAsync: _ =>
            {
                terminateCount++;
                return Task.CompletedTask;
            });
        var (window, terminateButton) = ShowRowAndGet<Button>(row, control =>
            string.Equals(AutomationProperties.GetName(control), "Terminate agent session", StringComparison.Ordinal));

        try
        {
            var interruptButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(control =>
                    string.Equals(AutomationProperties.GetName(control), "Interrupt agent", StringComparison.Ordinal));
            Assert.NotSame(interruptButton, terminateButton);
            Assert.True(terminateButton.IsEnabled);
            Assert.Equal("Terminate agent session", ToolTip.GetTip(terminateButton));
            Assert.Same(row.TerminateCommand, terminateButton.Command);

            terminateButton.Command!.Execute(terminateButton.CommandParameter);
            var command = Assert.IsType<AsyncRelayCommand>(row.TerminateCommand);
            await command.LastExecutionTask!;
            Assert.Equal(1, terminateCount);
            Assert.Equal(0, interruptCount);
            Assert.True(row.IsTerminationPending);
            Assert.False(terminateButton.IsEnabled);
            Assert.False(interruptButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void InterruptAndTerminateButtons_AreDistinctAndAccessible()
    {
        var axaml = File.ReadAllText(FindAxaml());

        Assert.Contains("Content=\"X\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Red\"", axaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Interrupt agent\"", axaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Terminate agent session\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding InterruptCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding TerminateCommand}\"", axaml, StringComparison.Ordinal);
    }

    private static RunningAgentRowViewModel CreateRemoteRow(
        bool continueInBackground = false,
        bool isInterruptible = false,
        bool isConnected = true,
        bool isTerminal = false,
        string entityName = "Remote session",
        Action? activate = null,
        Func<CancellationToken, Task>? interruptAsync = null,
        Func<CancellationToken, Task>? terminateAsync = null,
        Func<bool, CancellationToken, Task>? setContinueInBackgroundAsync = null)
    {
        var sessionId = new AgentSessionId(Guid.NewGuid().ToString("n"));
        var session = new RunningAgentChatWithEntityInfo(
            sessionId,
            isSubAgent: false,
            _ => throw new InvalidOperationException("The control test must not acquire a chat lease."),
            entityName,
            entityId: null)
        {
            IsRemote = true,
            CanSetContinueInBackground = true,
            ContinueInBackground = continueInBackground,
            ViewerCount = 1,
            IsInterruptible = isInterruptible,
            IsConnected = isConnected,
            IsTerminal = isTerminal,
        };

        return new RunningAgentRowViewModel(
            session,
            workspacePaneTitle: null,
            tabTitle: null,
            entityName: entityName,
            hasOpenTab: false,
            isThinking: isInterruptible,
            activateCommand: new RelayCommand(_ => activate?.Invoke()),
            interruptAsync: interruptAsync ?? (_ => Task.CompletedTask),
            terminateAsync: terminateAsync ?? (_ => Task.CompletedTask),
            setContinueInBackgroundAsync: setContinueInBackgroundAsync ?? ((_, _) => Task.CompletedTask));
    }

    private static (Window Window, T Control) ShowRowAndGet<T>(
        RunningAgentRowViewModel row,
        Func<T, bool> predicate)
        where T : Control
    {
        var (window, control) = ShowRows(row);
        return (window, window.GetVisualDescendants().OfType<T>().Single(predicate));
    }

    private static (Window Window, RunningAgentBrainControl Control) ShowRows(
        params RunningAgentRowViewModel[] rows)
    {
        var control = new RunningAgentBrainControl
        {
            DataContext = new RunningRowsViewModel(rows),
        };
        var window = new Window
        {
            Width = 500,
            Height = 500,
            Content = control,
        };
        window.Show();
        window.UpdateLayout();
        return (window, control);
    }

    private static Func<T, bool> HasClass<T>(string className)
        where T : StyledElement =>
        element => element.Classes.Contains(className);

    private static Control[] GetRowActions(Grid rowGrid) =>
        rowGrid.GetVisualDescendants()
            .OfType<Control>()
            .Where(control =>
                control.Classes.Contains("running-agent-row-action-button")
                || control.Classes.Contains("running-agent-row-action-toggle"))
            .ToArray();

    private static void DisableOpacityTransitions(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            control.Transitions = null;
        }
    }

    private static Rect BoundsRelativeTo(Control control, Visual ancestor)
    {
        var origin = control.TranslatePoint(default, ancestor);
        Assert.True(origin.HasValue);
        return new Rect(origin.Value, control.Bounds.Size);
    }

    private static Point PointInside(Control control, Visual ancestor)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
            ancestor);
        Assert.True(point.HasValue);
        return point.Value;
    }

    private sealed class RunningRowsViewModel(params RunningAgentRowViewModel[] rows)
    {
        public IReadOnlyList<RunningAgentRowViewModel> Rows { get; } = rows;
        public bool HasRows => true;
    }

    private static string FindAxaml()
    {
        var root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Phantom.Workspaces"))
               && Path.GetDirectoryName(root) is string parent)
        {
            root = parent;
        }

        return Path.Combine(root, "Phantom.Workspaces", "Controls", "RunningAgentBrainControl.axaml");
    }
}
