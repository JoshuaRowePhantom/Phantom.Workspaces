using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            "Remote session",
            entityId: null)
        {
            IsRemote = true,
            CanSetContinueInBackground = true,
            ContinueInBackground = continueInBackground,
            ViewerCount = 1,
            IsInterruptible = isInterruptible,
            IsConnected = true,
        };

        return new RunningAgentRowViewModel(
            session,
            workspacePaneTitle: null,
            tabTitle: null,
            entityName: "Remote session",
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
        var control = new RunningAgentBrainControl
        {
            DataContext = new RunningRowsViewModel(row),
        };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<T>().Single(predicate));
    }

    private sealed class RunningRowsViewModel(RunningAgentRowViewModel row)
    {
        public IReadOnlyList<RunningAgentRowViewModel> Rows { get; } = [row];
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
