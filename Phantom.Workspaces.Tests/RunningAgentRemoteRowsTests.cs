using System.Collections.ObjectModel;
using System.Windows.Input;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Moq;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentRemoteRowsTests
{
    [Fact]
    public void Refresh_RemoteTopLevelSession_CreatesRunningAgentRowWithRetentionState()
    {
        var table = new ControlledTable();
        var entry = table.Add("remote", isRemote: true, continueInBackground: true, viewerCount: 3);
        using var brain = Create(table);

        var row = Assert.Single(brain.Rows);
        Assert.True(row.IsRemote);
        Assert.True(row.ContinueInBackground);
        Assert.Equal(3, row.ViewerCount);
        Assert.True(row.IsBackgroundOptionEnabled);

        entry.SetViewerCount(4);

        Assert.Equal(4, row.ViewerCount);
    }

    [Fact]
    public async Task SetContinueInBackgroundCommand_UpdatePending_DisablesUntilAuthoritativeRefresh()
    {
        var table = new ControlledTable { DeferRetention = true };
        var entry = table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);
        var command = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(row.SetContinueInBackgroundCommand);

        command.Execute(true);
        await table.RetentionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(row.ContinueInBackground);
        Assert.False(row.IsBackgroundOptionEnabled);
        Assert.False(command.CanExecute(true));

        entry.SetViewerCount(2);
        Assert.False(row.IsBackgroundOptionEnabled);
        Assert.False(command.CanExecute(true));

        table.CompleteRetention();
        await command.LastExecutionTask!;

        Assert.True(row.ContinueInBackground);
        Assert.True(row.IsBackgroundOptionEnabled);
    }

    [Fact]
    public async Task SetContinueInBackgroundCommand_Rejected_KeepsAuthoritativeCheckedState()
    {
        var table = new ControlledTable { RetentionFailure = new InvalidOperationException("rejected") };
        table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);
        var command = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(row.SetContinueInBackgroundCommand);

        command.Execute(true);
        await command.LastExecutionTask!;

        Assert.False(row.ContinueInBackground);
        Assert.True(row.IsBackgroundOptionEnabled);
        Assert.Equal("Unable to update agent session.", row.LastOperationError);
    }

    [Fact]
    public async Task RetentionCompletion_WhileTerminationPending_DoesNotReenableActions()
    {
        var table = new ControlledTable { DeferRetention = true, DeferTermination = true };
        table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);
        var retention = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(
            row.SetContinueInBackgroundCommand);
        var terminate = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(
            row.TerminateCommand);

        retention.Execute(true);
        await table.RetentionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        terminate.Execute(null);
        await table.TerminationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        table.CompleteRetention();
        await retention.LastExecutionTask!;

        Assert.True(row.IsTerminationPending);
        Assert.False(row.IsInterruptEnabled);
        Assert.False(row.IsTerminateEnabled);
        Assert.False(row.IsBackgroundOptionEnabled);

        table.CompleteTermination();
        await terminate.LastExecutionTask!;
        Assert.Empty(brain.Rows);
    }

    [Fact]
    public async Task TerminateCommand_RemoteRow_TerminatesAndRemovesRow()
    {
        var table = new ControlledTable();
        table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);
        var command = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(row.TerminateCommand);

        command.Execute(null);
        await command.LastExecutionTask!;

        Assert.Equal("remote", Assert.Single(table.Terminated).Value);
        Assert.Empty(brain.Rows);
    }

    [Fact]
    public async Task TerminateCommand_WhenTerminationFails_ReenablesRowAndSurfacesError()
    {
        var table = new ControlledTable { TerminationFailure = new InvalidOperationException("rejected") };
        table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);
        var command = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(row.TerminateCommand);

        command.Execute(null);
        await command.LastExecutionTask!;

        Assert.False(row.IsTerminationPending);
        Assert.True(row.IsTerminateEnabled);
        Assert.True(command.CanExecute(null));
        Assert.Equal("Unable to terminate agent session.", row.LastOperationError);
    }

    [Fact]
    public async Task InterruptCommand_EnabledRow_InvokesCommonInterrupt()
    {
        var session = new RunningAgentChatWithEntityInfo(
            new RunningAgentChat(new AgentSessionId("remote"), null!),
            "Remote session",
            null)
        {
            IsRemote = true,
            ViewerCount = 1,
        };
        var interrupted = false;
        var row = new RunningAgentRowViewModel(
            session,
            null,
            null,
            "Remote session",
            hasOpenTab: false,
            isThinking: true,
            activateCommand: new Phantom.Workspaces.ViewModels.RelayCommand(_ => { }),
            interruptAsync: _ =>
            {
                interrupted = true;
                return Task.CompletedTask;
            },
            terminateAsync: _ => Task.CompletedTask,
            setContinueInBackgroundAsync: (_, _) => Task.CompletedTask);
        var command = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(row.InterruptCommand);

        command.Execute(null);
        await command.LastExecutionTask!;

        Assert.True(interrupted);
    }

    [Fact]
    public async Task InterruptCommand_RemoteChat_AwaitsAcknowledgementAndSurfacesFailure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.RunningItems)
            .Returns(new AgentChatRunningItemCollection { new AgentChatRunningItem() });
        chat.As<IAsyncInterruptibleAgentChat>()
            .Setup(value => value.InterruptAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.TrySetResult();
                return completion.Task;
            });
        chat.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var table = new ControlledTable { LeaseChat = chat.Object };
        var entry = table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        entry.SetIsInterruptible(true);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);
        var command = Assert.IsType<Phantom.Workspaces.ViewModels.AsyncRelayCommand>(row.InterruptCommand);

        command.Execute(null);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(command.LastExecutionTask!.IsCompleted);
        completion.TrySetException(new InvalidOperationException("owner rejected"));
        await command.LastExecutionTask;

        Assert.Equal("Unable to interrupt agent.", row.LastOperationError);
    }

    [Fact]
    public void InterruptCommand_NoInterruptibleRun_IsDisabled()
    {
        var table = new ControlledTable();
        table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        using var brain = Create(table);

        var row = Assert.Single(brain.Rows);

        Assert.False(row.IsInterruptEnabled);
        Assert.False(row.InterruptCommand.CanExecute(null));
    }

    [Fact]
    public void Refresh_BackgroundRemoteRunningSession_EnablesInterruptWithoutOpenTab()
    {
        var table = new ControlledTable();
        var entry = table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        entry.SetIsInterruptible(true);
        using var brain = Create(table);

        var row = Assert.Single(brain.Rows);

        Assert.True(row.IsThinking);
        Assert.True(row.IsInterruptEnabled);
    }

    [Fact]
    public void Refresh_DisconnectedOrTerminalRemoteSession_DisablesActions()
    {
        var table = new ControlledTable();
        var entry = table.Add("remote", isRemote: true, continueInBackground: false, viewerCount: 1);
        entry.SetIsInterruptible(true);
        using var brain = Create(table);
        var row = Assert.Single(brain.Rows);

        entry.SetIsConnected(false);
        Assert.False(row.IsInterruptEnabled);
        Assert.False(row.IsTerminateEnabled);

        entry.SetIsConnected(true);
        entry.SetIsTerminal(true);
        Assert.False(row.IsInterruptEnabled);
        Assert.False(row.IsTerminateEnabled);
    }

    [Fact]
    public void Refresh_LocalSessionWithViewersButNoRetentionCapability_DisablesBackgroundOption()
    {
        var table = new ControlledTable();
        table.Add("local", isRemote: false, continueInBackground: false, viewerCount: 2);
        using var brain = Create(table);

        Assert.False(Assert.Single(brain.Rows).IsBackgroundOptionEnabled);
    }

    [Fact]
    public void Refresh_LocalTopLevelSessionWithRetentionCapability_EnablesBackgroundOption()
    {
        var table = new ControlledTable();
        var entry = table.Add("local", isRemote: false, continueInBackground: false, viewerCount: 0);
        entry.SetCanSetContinueInBackground(true);
        using var brain = Create(table);

        Assert.True(Assert.Single(brain.Rows).IsBackgroundOptionEnabled);
    }

    private static RunningAgentBrainViewModel Create(ControlledTable table) =>
        new(table, () => [], new FakeTabNavigator(), action => action());

    private sealed class ControlledTable : IRunningAgentChatTable
    {
        private RunningAgentChatWithEntityInfo? entry;
        public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; } = [];
        public TaskCompletionSource RetentionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TerminationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DeferRetention { get; init; }
        public bool DeferTermination { get; init; }
        public Exception? RetentionFailure { get; init; }
        public Exception? TerminationFailure { get; init; }
        public IAgentChat? LeaseChat { get; init; }
        public List<AgentSessionId> Terminated { get; } = [];

        public RunningAgentChatWithEntityInfo Add(
            string sessionId,
            bool isRemote,
            bool continueInBackground,
            int viewerCount)
        {
            var id = new AgentSessionId(sessionId);
            RunningAgentChatWithEntityInfo value;
            if (this.LeaseChat is null)
            {
                value = new RunningAgentChatWithEntityInfo(
                    new RunningAgentChat(id, null!),
                    "Remote session",
                    null)
                {
                    IsRemote = isRemote,
                    CanSetContinueInBackground = isRemote,
                    ContinueInBackground = continueInBackground,
                    ViewerCount = viewerCount,
                };
            }
            else
            {
                value = new RunningAgentChatWithEntityInfo(
                    id,
                    isSubAgent: false,
                    _ => Task.FromResult(new RunningAgentChatLease(
                        id,
                        this.LeaseChat,
                        () => ValueTask.CompletedTask)),
                    "Remote session",
                    entityId: null)
                {
                    IsRemote = isRemote,
                    CanSetContinueInBackground = isRemote,
                    ContinueInBackground = continueInBackground,
                    ViewerCount = viewerCount,
                };
            }
            this.entry = value;
            this.RunningSessions.Add(value);
            return value;
        }

        public Task<RunningAgentChatLease> AcquireAsync(
            AcquireAgentChatRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<bool> TerminateAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            this.TerminationStarted.TrySetResult();
            if (this.TerminationFailure is not null)
            {
                throw this.TerminationFailure;
            }
            if (this.DeferTermination)
            {
                await this.terminationCompletion.Task.WaitAsync(ct);
            }
            this.Terminated.Add(sessionId);
            var match = this.RunningSessions.SingleOrDefault(
                session => session.SessionId == sessionId);
            if (match is not null)
            {
                this.RunningSessions.Remove(match);
            }
            return match is not null;
        }

        public async Task SetContinueInBackgroundAsync(
            AgentSessionId sessionId,
            bool continueInBackground,
            CancellationToken ct = default)
        {
            this.RetentionStarted.TrySetResult();
            if (this.RetentionFailure is not null)
            {
                throw this.RetentionFailure;
            }
            if (this.DeferRetention)
            {
                await this.retentionCompletion.Task.WaitAsync(ct);
            }
            this.entry!.SetContinueInBackground(continueInBackground);
        }

        private readonly TaskCompletionSource retentionCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource terminationCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteRetention() => this.retentionCompletion.TrySetResult();
        public void CompleteTermination() => this.terminationCompletion.TrySetResult();
    }
}
