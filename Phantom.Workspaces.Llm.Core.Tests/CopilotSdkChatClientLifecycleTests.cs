using System.Collections.ObjectModel;
using System.Reflection;
using AgentSchema;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

#pragma warning disable GHCP001
public sealed partial class CopilotSdkChatClientTests
{
    private sealed class LifecycleUiLifetime(IDisposable value, TaskScheduler scheduler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(Task.Factory.StartNew(value.Dispose,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler));
    }

    private sealed class LifecyclePanelSink(Func<bool> isNonrunning) : IChatOutputHtmlSink
    {
        public TaskCompletionSource Removed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content) { }
        public void ScrollToBottom() { }
        public void RemoveContent(string path)
        {
            if (path == ChatOutputHtmlRenderer.SubAgentPanelInnerId && isNonrunning()) this.Removed.TrySetResult();
        }
    }

    private static readonly DateTimeOffset LifecycleTime = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static SubagentStartedEvent LifecycleStart(string call = "call", int epoch = 0) => new()
    {
        Id = Guid.NewGuid(), AgentId = "child", Timestamp = LifecycleTime.AddSeconds(epoch),
        Data = new() { ToolCallId = call, AgentName = "task", AgentDisplayName = "Task", AgentDescription = "test" },
    };

    private static SubagentCompletedEvent LifecycleFinish(string call = "call", int epoch = 1, bool cancelled = false) => new()
    {
        Id = Guid.NewGuid(), AgentId = "child", Timestamp = LifecycleTime.AddSeconds(epoch),
        Data = new() { ToolCallId = call, AgentName = "task", AgentDisplayName = "Task", Cancelled = cancelled },
    };

    private static TaskInfoAgent LifecycleTask(GitHub.Copilot.Rpc.TaskStatus status) => new()
    {
        Id = "task", ToolCallId = "call", AgentType = "task", Prompt = "synthetic",
        Description = "test", StartedAt = LifecycleTime, Status = status,
    };

    private static async Task EmitObservedAsync(CopilotSdkChatClient client, Infrastructure.FakeCopilotSession session, SessionEvent value)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnObserved(Guid id) { if (id == value.Id) observed.TrySetResult(); }
        client.StateEventProcessed += OnObserved;
        try
        {
            session.Emit(value);
            await observed.Task;
        }
        finally { client.StateEventProcessed -= OnObserved; }
    }

    private static void RegisterLifecycleChild(CopilotSdkChatClient client, SubAgent child) =>
        typeof(CopilotSdkChatClient).GetMethod("RegisterSubAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, ["child", "call", child]);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CopilotSdkChatClient_BackgroundInvalidationPrecedesCompletion_ConvergesAfterReplayGap(bool cancelled, bool live)
    {
        var session = new Infrastructure.FakeCopilotSession();
        var sdk = new Infrastructure.FakeCopilotClient(session)
        {
            ReadEventsHandler = (_, _) => Task.FromResult(new EventsReadResult { Events = [], Cursor = "tail" }),
        };
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(sdk));
        await InvokeEnsureSessionAsync(client);
        var foreground = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        await using var liveChat = live ? await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"hosted","model":{"id":"github-copilot-subagent","provider":"github-copilot-subagent"},"tools":[]}
                """),
            ConfiguredStore = new InMemoryAgentPersistenceStore(), ClientOverride = new CopilotSubAgentChatClient(),
            ForegroundScheduler = foreground,
        }) : null;
        var child = liveChat is null
            ? new SubAgent(CopilotSubAgentIdentity.Create("child", "call"), null)
            : new SubAgent(new AgentSessionId(liveChat.AgentSessionId), liveChat, null);
        var originalIdentity = child.SessionId;
        if (live) RegisterLifecycleChild(client, child);
        else client.RegisterRestoredSubAgent(child);
        var started = LifecycleStart();
        var liveStarted = liveChat is null ? Task.CompletedTask
            : WaitForCompletionStateAsync(liveChat, AgentChatCompletionState.Running, CancellationToken.None);
        await EmitObservedAsync(client, session, started);
        await liveStarted;
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)child).CompletionState);
        var history = new ObservableCollection<IRunningSubAgent> { child };
        var historyMutations = 0;
        history.CollectionChanged += (_, _) => historyMutations++;
        var browser = await Task.Factory.StartNew(
            () => new SubAgentBrowserViewModel(new(history)) { HideCompleted = true },
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, foreground);
        await using var browserLifetime = new LifecycleUiLifetime(browser, foreground);
        var display = await Task.Factory.StartNew<IRunningSubAgentDisplay>(
            () => liveChat is null ? new RemoteRunningSubAgentDisplay(child) : new RunningSubAgentDisplay(liveChat),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, foreground);
        await using var displayLifetime = new LifecycleUiLifetime((IDisposable)display, foreground);
        var displays = new ObservableCollection<IRunningSubAgentDisplay> { display };
        var sink = new LifecyclePanelSink(() => ((IRunningSubAgent)child).CompletionState != AgentChatCompletionState.Running);
        var footer = await Task.Factory.StartNew(
            () => new RunningSubAgentsHtmlTransformer(displays, [], sink),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, foreground);
        await using var footerLifetime = new LifecycleUiLifetime(footer, foreground);
        Assert.Single(browser.VisibleItems);
        var invalidation = new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), ParentId = started.Id, Data = new(),
        };
        await EmitObservedAsync(client, session, invalidation);
        var completed = LifecycleFinish(cancelled: cancelled);
        completed.ParentId = Guid.NewGuid();
        await EmitObservedAsync(client, session, completed);
        await sink.Removed.Task;
        Assert.Equal(cancelled ? AgentChatCompletionState.Failed : AgentChatCompletionState.Succeeded,
            ((IRunningSubAgent)child).CompletionState);
        Assert.True(client.IsInvocationTerminal("call"));
        Assert.Equal(originalIdentity, child.SessionId);
        Assert.Empty(browser.VisibleItems);
        Assert.Equal(0, historyMutations);
        Assert.Same(child, Assert.Single(history));
        browser.HideCompleted = false;
        Assert.Same(child, Assert.Single(browser.VisibleItems));
    }

    [Fact]
    public async Task CopilotSdkChatClient_BetweenTurnsCancelledCompletion_PreservesCancelledOutcome()
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(
            new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        var definition = AgentDefinitionLoader.LoadAgentFromJson("""
            {"kind":"prompt","name":"hosted","model":{"id":"github-copilot-subagent","provider":"github-copilot-subagent"},"tools":[]}
            """);
        var receiver = new CopilotSubAgentChatClient();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = definition, ConfiguredStore = new InMemoryAgentPersistenceStore(), ClientOverride = receiver,
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        RegisterLifecycleChild(client, child);
        await EmitObservedAsync(client, session, LifecycleStart());
        var done = WaitForCompletionStateAsync(chat, AgentChatCompletionState.Failed, CancellationToken.None);
        await EmitObservedAsync(client, session, LifecycleFinish(cancelled: true));
        await done;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in receiver.GetStreamingResponseAsync([])) { }
        });
        Assert.True(client.IsInvocationTerminal("call"));
        Assert.Equal(AgentChatCompletionState.Failed, child.AgentChat!.CompletionState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotSdkChatClient_OlderCompletionAfterResumption_DoesNotStopNewEpoch(bool cancelled)
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(
            new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        var child = new SubAgent(CopilotSubAgentIdentity.Create("child", "call"), null);
        client.RegisterRestoredSubAgent(child);
        await EmitObservedAsync(client, session, LifecycleStart());
        await EmitObservedAsync(client, session, LifecycleFinish());
        await EmitObservedAsync(client, session, LifecycleStart(epoch: 3));
        var late = LifecycleFinish(epoch: 2, cancelled: cancelled);
        await EmitObservedAsync(client, session, late);
        await EmitObservedAsync(client, session, late);
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)child).CompletionState);
        Assert.False(client.IsInvocationTerminal("call"));
    }

    [Fact]
    public async Task CopilotSdkChatClient_OlderCancellationAfterResumption_DoesNotStopNewInvocation()
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(
            new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        var child = new SubAgent(CopilotSubAgentIdentity.Create("child", "call"), null);
        client.RegisterRestoredSubAgent(child);
        await EmitObservedAsync(client, session, LifecycleStart());
        await EmitObservedAsync(client, session, LifecycleFinish(cancelled: true));
        await EmitObservedAsync(client, session, LifecycleStart("new-call", 3));
        await EmitObservedAsync(client, session, LifecycleFinish(epoch: 2, cancelled: true));
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)child).CompletionState);
        Assert.False(client.IsInvocationTerminal("new-call"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotSdkChatClient_TaskOnlyStableIdResumption_RejectsOlderTerminalReceiverDispatch(bool cancelled)
    {
        var session = new Infrastructure.FakeCopilotSession { Tasks = [LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running)] };
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        var receiver = new CopilotSubAgentChatClient();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"hosted","model":{"id":"github-copilot-subagent","provider":"github-copilot-subagent"},"tools":[]}
                """),
            ConfiguredStore = new InMemoryAgentPersistenceStore(), ClientOverride = receiver,
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        RegisterLifecycleChild(client, child);
        await EmitObservedAsync(client, session, LifecycleStart());
        await EmitObservedAsync(client, session, LifecycleFinish());
        session.Tasks = [LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Idle)];
        await EmitObservedAsync(client, session, new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), Timestamp = LifecycleTime.AddSeconds(2), Data = new(),
        });
        session.Tasks = [LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running)];
        var running = WaitForCompletionStateAsync(chat, AgentChatCompletionState.Running, CancellationToken.None);
        await EmitObservedAsync(client, session, new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), Timestamp = LifecycleTime.AddSeconds(4), Data = new(),
        });
        await running;
        var old = LifecycleFinish(epoch: 3, cancelled: cancelled);
        await EmitObservedAsync(client, session, old);
        await EmitObservedAsync(client, session, old);
        Assert.False(receiver.BeginInvocation());
        Assert.False(client.IsInvocationTerminal("call"));
        Assert.Equal(AgentChatCompletionState.Running, chat.CompletionState);
        var completed = WaitForCompletionStateAsync(chat, AgentChatCompletionState.Succeeded, CancellationToken.None);
        await EmitObservedAsync(client, session, LifecycleFinish(epoch: 5));
        await completed;
        Assert.Same(chat, child.AgentChat);
    }

    [Fact]
    public async Task CopilotSdkChatClient_AgentIdleNotificationWithEvictedTask_ReconcilesCorrelatedInvocation()
    {
        await AssertTaskNotificationAsync("idle");
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task CopilotSdkChatClient_AgentCompletedNotificationAfterParentTurn_ReconcilesSameChild(string outcome)
    {
        await AssertTaskNotificationAsync(outcome);
    }

    private static async Task AssertTaskNotificationAsync(string outcome)
    {
        var session = new Infrastructure.FakeCopilotSession
        {
            Tasks = [LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running)],
        };
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(
            new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        var child = new SubAgent(CopilotSubAgentIdentity.Create("child", "call"), null);
        client.RegisterRestoredSubAgent(child);
        var other = new SubAgent(CopilotSubAgentIdentity.Create("other", "other-call"), null);
        client.RegisterRestoredSubAgent(other);
        await EmitObservedAsync(client, session, new SubagentStartedEvent
        {
            Id = Guid.NewGuid(), AgentId = "other", Timestamp = LifecycleTime,
            Data = new() { ToolCallId = "other-call", AgentName = "task", AgentDisplayName = "Other", AgentDescription = "test" },
        });
        await EmitObservedAsync(client, session, LifecycleStart());
        session.Tasks = [];
        await EmitObservedAsync(client, session, new SessionBackgroundTasksChangedEvent { Id = Guid.NewGuid(), Data = new() });
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)child).CompletionState);
        SystemNotification kind = outcome == "idle"
            ? new SystemNotificationAgentIdle { AgentId = "task", AgentType = "task" }
            : new SystemNotificationAgentCompleted
            {
                AgentId = "task", AgentType = "task", Status = new SystemNotificationAgentCompletedStatus(outcome),
            };
        var notification = new SystemNotificationEvent
        {
            Id = Guid.NewGuid(), AgentId = "not-a-child", Timestamp = LifecycleTime.AddSeconds(1),
            Data = new() { Content = "synthetic prose must not be used for correlation", Kind = kind },
        };
        await EmitObservedAsync(client, session, notification);
        Assert.Equal(outcome == "failed" ? AgentChatCompletionState.Failed : AgentChatCompletionState.Succeeded,
            ((IRunningSubAgent)child).CompletionState);
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)other).CompletionState);
        await EmitObservedAsync(client, session, LifecycleStart(epoch: 3));
        notification.Id = Guid.NewGuid();
        notification.Timestamp = LifecycleTime.AddSeconds(2);
        await EmitObservedAsync(client, session, notification);
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)child).CompletionState);
    }

    [Fact]
    public async Task CopilotSdkChatClient_RestoreAndReconnectWithUnclosedInvocation_UsesCurrentExecutionState()
    {
        var start = LifecycleStart();
        var session = new Infrastructure.FakeCopilotSession();
        var sdk = new Infrastructure.FakeCopilotClient(session)
        {
            ReadEventsHandler = (_, _) => Task.FromResult(new EventsReadResult { Events = [start], Cursor = "tail" }),
            LiveSessionStates = [new() { SessionId = "child", Status = AgentRegistryLiveTargetEntryStatus.Done }],
        };
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(sdk));
        client.SetResumeSessionId("previous");
        await InvokeEnsureSessionAsync(client);
        var child = new SubAgent(CopilotSubAgentIdentity.Create("child", "call"), null);
        client.RegisterRestoredSubAgent(child);
        Assert.Equal(AgentChatCompletionState.Unknown, ((IRunningSubAgent)child).CompletionState);
        await EmitObservedAsync(client, session, LifecycleStart(epoch: 3));
        Assert.Equal(AgentChatCompletionState.Running, ((IRunningSubAgent)child).CompletionState);
    }

    [Fact]
    public async Task CopilotSdkChatClient_ObserverReconnect_DoesNotReuseFailedTerminalBarriers()
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await (Task)typeof(CopilotSdkChatClient).GetMethod("StopStateObserverAsync", flags)!.Invoke(client, null)!;
        var registeredSession = typeof(CopilotSdkChatClient).GetField("copilotSession", flags)!.GetValue(client)!;
        typeof(CopilotSdkChatClient).GetMethod("InvalidateCopilotSession", flags)!.Invoke(client, [registeredSession]);
        var failure = typeof(CopilotSdkChatClient).GetField("stateObserverFailure", flags)!;
        failure.SetValue(client, new IOException("synthetic disconnected observer"));
        var barriers = (Dictionary<Guid, TaskCompletionSource>)typeof(CopilotSdkChatClient)
            .GetField("terminalObservationBarriers", flags)!.GetValue(client)!;
        var obsolete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        barriers.Add(Guid.NewGuid(), obsolete);
        await InvokeEnsureSessionAsync(client);
        Assert.Null(failure.GetValue(client));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete.Task);
        await EmitObservedAsync(client, session, LifecycleStart());
    }
}
