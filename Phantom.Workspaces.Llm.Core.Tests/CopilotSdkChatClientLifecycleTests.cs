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
        public bool RunningPanel { get; private set; }
        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content) => this.RunningPanel = true;
        public void ScrollToBottom() { }
        public void RemoveContent(string path)
        {
            if (path == ChatOutputHtmlRenderer.SubAgentPanelInnerId) this.RunningPanel = false;
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

    private static AgentDefinition LifecycleDefinition() => AgentDefinitionLoader.LoadAgentFromJson("""
        {"kind":"prompt","name":"hosted","model":{"id":"github-copilot-subagent","provider":"github-copilot-subagent"},"tools":[]}
        """);

    private sealed class LifecycleInlineScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => this.TryExecuteTask(task);
        protected override void QueueTask(Task task) => this.TryExecuteTask(task);
    }

    private sealed class LifecycleUiProjection : IDisposable
    {
        private readonly RemoteRunningSubAgentDisplay display;
        private readonly RunningSubAgentsHtmlTransformer footer;
        public SubAgentBrowserViewModel Browser { get; }
        public LifecyclePanelSink Sink { get; }
        public LifecycleUiProjection(SubAgent child)
        {
            this.Browser = new(new(new ObservableCollection<IRunningSubAgent> { child })) { HideCompleted = true };
            this.display = new(child);
            this.Sink = new(() => ((IRunningSubAgent)child).CompletionState != AgentChatCompletionState.Running);
            this.footer = new([this.display], [], this.Sink);
        }
        public void Dispose()
        {
            this.footer.Dispose();
            this.display.Dispose();
            this.Browser.Dispose();
        }
    }

    private static Task<LifecycleUiProjection> ObserveLifecycleUiAsync(SubAgent child) =>
        Task.Factory.StartNew(() => new LifecycleUiProjection(child), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, new LifecycleInlineScheduler());

    private static async Task SnapshotAsync(CopilotSdkChatClient client, Infrastructure.FakeCopilotSession session,
        int epoch, params TaskInfo[] tasks)
    {
        session.Tasks = tasks;
        await EmitObservedAsync(client, session, new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), Timestamp = LifecycleTime.AddSeconds(epoch), Data = new(),
        });
    }

    private static async Task RouterBarrierAsync(CopilotSdkChatClient client, Infrastructure.FakeCopilotSession session,
        Task<bool> pending, int epoch)
    {
        await EmitObservedAsync(client, session, new AssistantMessageDeltaEvent
        {
            Id = Guid.NewGuid(), Timestamp = LifecycleTime.AddSeconds(epoch),
            Data = new() { MessageId = "router-barrier", DeltaContent = "router ready" },
        });
        Assert.True(await pending);
    }

    private sealed class LifecycleParentTurn : IAsyncDisposable
    {
        private readonly Infrastructure.FakeCopilotSession session;
        private readonly IAsyncEnumerator<ChatResponseUpdate> enumerator;
        public Task<bool>? Pending { get; set; }
        public LifecycleParentTurn(CopilotSdkChatClient client, Infrastructure.FakeCopilotSession session, bool active)
        {
            this.session = session;
            this.enumerator = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "synthetic")]).GetAsyncEnumerator();
            if (active) this.Next();
        }
        public void Next() => this.Pending = this.enumerator.MoveNextAsync().AsTask();
        public async ValueTask DisposeAsync()
        {
            if (this.Pending is not null)
            {
                this.session.Emit(new SessionIdleEvent { Id = Guid.NewGuid(), Timestamp = LifecycleTime.AddSeconds(7), Data = new() });
                while (await this.Pending) this.Next();
            }
            await this.enumerator.DisposeAsync();
        }
    }

    private sealed class DeferredLifecycleFactory : IRunningAgentChatFactory
    {
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InMemoryAgentPersistenceStore Store { get; } = new();
        public CopilotSubAgentChatClient Receiver { get; } = new();
        public RunningAgentChatLease? Lease { get; private set; }
        public int Releases { get; private set; }
        public async Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId,
            AgentServices? services, string? displayNameOverride, string? descriptionOverride, string? nameOverride,
            CancellationToken ct)
        {
            this.Entered.TrySetResult();
            await this.Continue.Task;
            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentSessionId = sessionId.Value, AgentDefinition = LifecycleDefinition(), ConfiguredStore = this.Store,
                ClientOverride = this.Receiver, ForegroundScheduler = new LifecycleInlineScheduler(),
            });
            return this.Lease = new(sessionId, chat, () =>
            {
                this.Releases++;
                return ValueTask.CompletedTask;
            });
        }
        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent, CancellationToken ct) =>
            this.Lease is { } lease && lease.SessionId == sessionId ? Task.FromResult(lease) : throw new InvalidOperationException();
        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition,
            AgentServices? services, string? displayNameOverride, string? descriptionOverride, bool registerAsRunningAgent,
            CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("cancelled", false)]
    [InlineData("failed", false)]
    [InlineData("completed", true)]
    [InlineData("cancelled", true)]
    [InlineData("failed", true)]
    public async Task CopilotSdkChatClient_TerminalBeforeMaterialization_FinishesCanonicalReceiverByInvocation(
        string outcome, bool toolCallOnly)
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        var factory = new DeferredLifecycleFactory();
        var table = new SubAgentTestFakes.FakeSubAgentTable();
        client.SetSubAgentDependencies(factory, table);
        await InvokeEnsureSessionAsync(client);
        await using var parent = new LifecycleParentTurn(client, session, true);
        await EmitObservedAsync(client, session, LifecycleStart());
        await factory.Entered.Task;
        SessionEvent terminal = outcome == "failed" ? new SubagentFailedEvent
        {
            Id = Guid.NewGuid(), AgentId = toolCallOnly ? null : "child", Timestamp = LifecycleTime.AddSeconds(1),
            Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", Error = "retained failure diagnostic" },
        } : LifecycleFinish(cancelled: outcome == "cancelled");
        if (toolCallOnly) terminal.AgentId = null;
        await EmitObservedAsync(client, session, terminal);
        factory.Continue.TrySetResult();
        await RouterBarrierAsync(client, session, parent.Pending!, 2);
        parent.Next();
        await using var chat = Assert.Single(table.AddedChats);
        Assert.Equal(outcome == "completed" ? AgentChatCompletionState.Succeeded : AgentChatCompletionState.Failed, chat.CompletionState);
        await using (var output = factory.Receiver.GetStreamingResponseAsync([]).GetAsyncEnumerator())
        {
            var finished = output.MoveNextAsync();
            Assert.True(finished.IsCompleted);
            if (outcome == "completed") Assert.False(await finished);
            else if (outcome == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await finished);
            else Assert.Contains("retained failure diagnostic",
                (await Assert.ThrowsAsync<AgentSubagentFailedException>(async () => await finished)).Message);
        }
        var router = (CopilotSubAgentRouter)typeof(CopilotSdkChatClient)
            .GetField("currentRouter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        await router.TerminalizeRemainingChildrenAsync(new OperationCanceledException());
        await chat.WaitForLifecyclePersistenceAsync();
        Assert.Equal(outcome, (await factory.Store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
        Assert.Equal(1, factory.Releases);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task CopilotSdkChatClient_EvictedTaskResumption_ClosesFreshReceiverNotOlderGeneration(
        bool priorCancelled, bool cancelled, bool parentTurn)
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        await InvokeEnsureSessionAsync(client);
        var store = new InMemoryAgentPersistenceStore();
        var receiver = new CopilotSubAgentChatClient();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = LifecycleDefinition(), ConfiguredStore = store, ClientOverride = receiver,
            ForegroundScheduler = new LifecycleInlineScheduler(),
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        var transcript = new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [new TextContent("retained history")] };
        chat.History.Add(transcript);
        using var ui = await ObserveLifecycleUiAsync(child);
        RegisterLifecycleChild(client, child);
        var history = new ObservableCollection<IRunningSubAgent> { child };
        var mutations = 0;
        history.CollectionChanged += (_, _) => mutations++;
        await using var parent = new LifecycleParentTurn(client, session, parentTurn);
        await EmitObservedAsync(client, session, LifecycleStart());
        if (parentTurn)
        {
            await RouterBarrierAsync(client, session, parent.Pending!, 0);
            parent.Next();
        }
        await SnapshotAsync(client, session, 0, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        if (priorCancelled) await EmitObservedAsync(client, session, LifecycleFinish(cancelled: true));
        else await SnapshotAsync(client, session, 1, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Idle));
        await SnapshotAsync(client, session, 2);
        await SnapshotAsync(client, session, 4, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        Assert.True(ui.Sink.RunningPanel);
        Assert.Single(ui.Browser.VisibleItems);
        Assert.False(client.IsInvocationTerminal("call"));
        Assert.False(receiver.BeginInvocation());
        var late = LifecycleFinish(epoch: 3, cancelled: true);
        await EmitObservedAsync(client, session, late);
        await EmitObservedAsync(client, session, late);
        Assert.False(receiver.BeginInvocation());
        Assert.False(client.IsInvocationTerminal("call"));
        await EmitObservedAsync(client, session, LifecycleFinish(epoch: 5, cancelled: cancelled));
        Assert.True(client.IsInvocationTerminal("call"));
        // Inspect the actual channel without relying on a timeout to detect a missed termination.
        await using var output = receiver.GetStreamingResponseAsync([]).GetAsyncEnumerator();
        var finished = output.MoveNextAsync();
        Assert.True(finished.IsCompleted);
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await finished);
        else Assert.False(await finished);
        await chat.WaitForLifecyclePersistenceAsync();
        var raw = cancelled ? "cancelled" : "completed";
        Assert.Equal(raw, (await store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
        await using var restored = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = chat.AgentSessionId, AgentDefinition = LifecycleDefinition(),
            ConfiguredStore = store, ClientOverride = new CopilotSubAgentChatClient(),
        });
        Assert.Equal(cancelled ? AgentChatCompletionState.Failed : AgentChatCompletionState.Succeeded, restored.CompletionState);
        Assert.Same(chat, child.AgentChat);
        Assert.Same(child, Assert.Single(history));
        Assert.Equal(0, mutations);
        Assert.Contains(transcript, chat.History);
        Assert.False(ui.Sink.RunningPanel);
        Assert.Empty(ui.Browser.VisibleItems);
        if (parent.Pending is not null)
        {
            await RouterBarrierAsync(client, session, parent.Pending, 6);
            parent.Next();
            var router = (CopilotSubAgentRouter)typeof(CopilotSdkChatClient)
                .GetField("currentRouter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            await router.TerminalizeRemainingChildrenAsync(new OperationCanceledException());
            await EmitObservedAsync(client, session, late);
            await chat.WaitForLifecyclePersistenceAsync();
            Assert.Equal(raw, (await store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
        }
    }

    [Theory]
    [InlineData("cancelled", false, false, false)]
    [InlineData("cancelled", true, false, false)]
    [InlineData("cancelled", false, true, false)]
    [InlineData("failed", false, false, false)]
    [InlineData("failed", true, false, false)]
    [InlineData("failed", false, true, false)]
    [InlineData("cancelled", false, true, true)]
    [InlineData("failed", true, false, true)]
    public async Task CopilotSdkChatClient_HistoricalCompletedTask_PreservesLatestPersistedOutcome(
        string outcome, bool reverse, bool evictLatest, bool parentTurn)
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        await InvokeEnsureSessionAsync(client);
        var store = new InMemoryAgentPersistenceStore();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = LifecycleDefinition(), ConfiguredStore = store, ClientOverride = new CopilotSubAgentChatClient(),
            ForegroundScheduler = new LifecycleInlineScheduler(),
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        using var ui = await ObserveLifecycleUiAsync(child);
        RegisterLifecycleChild(client, child);
        await using var parent = new LifecycleParentTurn(client, session, parentTurn);
        await EmitObservedAsync(client, session, LifecycleStart());
        if (parent.Pending is not null)
        {
            await RouterBarrierAsync(client, session, parent.Pending, 0);
            parent.Next();
        }
        await EmitObservedAsync(client, session, LifecycleFinish());
        await EmitObservedAsync(client, session, LifecycleStart("new-call", 3));
        typeof(CopilotSdkChatClient).GetMethod("RegisterSubAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, ["child", "new-call", child]);
        SessionEvent terminal = outcome == "cancelled" ? LifecycleFinish("new-call", 4, true) : new SubagentFailedEvent
        {
            Id = Guid.NewGuid(), AgentId = "child", Timestamp = LifecycleTime.AddSeconds(4),
            Data = new() { ToolCallId = "new-call", AgentName = "task", AgentDisplayName = "Task", Error = "synthetic" },
        };
        await EmitObservedAsync(client, session, terminal);
        var old = LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Completed);
        var current = LifecycleTask(new GitHub.Copilot.Rpc.TaskStatus(outcome));
        current.Id = "new-task";
        current.ToolCallId = "new-call";
        await SnapshotAsync(client, session, 5, evictLatest ? [old] : reverse ? [current, old] : [old, current]);
        await using (var output = ((CopilotSubAgentChatClient)chat.GetService(typeof(ICopilotSubAgentReceiver))!)
            .GetStreamingResponseAsync([]).GetAsyncEnumerator())
        {
            var finished = output.MoveNextAsync();
            Assert.True(finished.IsCompleted);
            if (outcome == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await finished);
            else await Assert.ThrowsAsync<AgentSubagentFailedException>(async () => await finished);
        }
        await chat.WaitForLifecyclePersistenceAsync();
        Assert.Equal(outcome, (await store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
        await using var restored = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = chat.AgentSessionId, AgentDefinition = LifecycleDefinition(),
            ConfiguredStore = store, ClientOverride = new CopilotSubAgentChatClient(),
        });
        Assert.Equal(AgentChatCompletionState.Failed, restored.CompletionState);
        Assert.False(ui.Sink.RunningPanel);
        Assert.Empty(ui.Browser.VisibleItems);
        Assert.Same(chat, child.AgentChat);
        if (parent.Pending is not null)
        {
            await RouterBarrierAsync(client, session, parent.Pending, 6);
            parent.Next();
            await chat.WaitForLifecyclePersistenceAsync();
            Assert.Equal(outcome, (await store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
        }
    }

    [Theory]
    [InlineData("idle", false)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("idle", true)]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    public async Task CopilotSdkChatClient_EvictedTaskNotification_TerminatesBoundReceiverAndPermitsFollowUp(
        string outcome, bool parentTurn)
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        await InvokeEnsureSessionAsync(client);
        var receiver = new CopilotSubAgentChatClient();
        var store = new InMemoryAgentPersistenceStore();
        var id = CopilotSubAgentIdentity.Create("child", "call").Value;
        if (parentTurn) await store.StoreAsync(new()
        {
            Agent = new() { AgentSessionId = id, SubAgentLifecycleState = "completed" },
        });
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = id, AgentDefinition = LifecycleDefinition(), ConfiguredStore = store, ClientOverride = receiver,
            ForegroundScheduler = new LifecycleInlineScheduler(),
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        using var ui = await ObserveLifecycleUiAsync(child);
        RegisterLifecycleChild(client, child);
        await using var parent = new LifecycleParentTurn(client, session, parentTurn);
        await EmitObservedAsync(client, session, LifecycleStart());
        if (parentTurn)
        {
            await RouterBarrierAsync(client, session, parent.Pending!, 0);
            parent.Next();
        }
        await SnapshotAsync(client, session, 0, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        await SnapshotAsync(client, session, 1);
        SystemNotification kind = outcome == "idle"
            ? new SystemNotificationAgentIdle { AgentId = "task", AgentType = "task" }
            : new SystemNotificationAgentCompleted { AgentId = "task", AgentType = "task", Status = new(outcome) };
        await EmitObservedAsync(client, session, new SystemNotificationEvent
        {
            Id = Guid.NewGuid(), AgentId = "not-a-child", Timestamp = LifecycleTime.AddSeconds(2),
            Data = new() { Kind = kind, Content = "not correlation" },
        });
        await using (var output = receiver.GetStreamingResponseAsync([]).GetAsyncEnumerator())
        {
            var finished = output.MoveNextAsync();
            Assert.True(finished.IsCompleted);
            if (outcome == "failed") await Assert.ThrowsAsync<AgentSubagentFailedException>(async () => await finished);
            else Assert.False(await finished);
        }
        await chat.WaitForLifecyclePersistenceAsync();
        Assert.False(ui.Sink.RunningPanel);
        Assert.Empty(ui.Browser.VisibleItems);
        Assert.Equal(outcome == "failed" ? "failed" : "completed",
            (await store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
        await SnapshotAsync(client, session, 4, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        Assert.False(client.IsInvocationTerminal("call"));
        Assert.False(receiver.BeginInvocation());
        Assert.True(ui.Sink.RunningPanel);
        Assert.Single(ui.Browser.VisibleItems);
        await EmitObservedAsync(client, session, LifecycleFinish(epoch: 3, cancelled: true));
        Assert.False(receiver.BeginInvocation());
        await EmitObservedAsync(client, session, LifecycleFinish(epoch: 5));
        Assert.Same(chat, child.AgentChat);
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("task-resumption")]
    [InlineData("independent-task")]
    public async Task CopilotSdkChatClient_EvictedNotificationWithCurrentWork_DoesNotCloseLiveReceiver(string work)
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        await InvokeEnsureSessionAsync(client);
        var receiver = new CopilotSubAgentChatClient();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = LifecycleDefinition(), ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = receiver, ForegroundScheduler = new LifecycleInlineScheduler(),
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        RegisterLifecycleChild(client, child);
        await EmitObservedAsync(client, session, LifecycleStart());
        await SnapshotAsync(client, session, 0, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        if (work == "task-resumption")
            await SnapshotAsync(client, session, 1, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Idle));
        await SnapshotAsync(client, session, 2);
        if (work == "invocation") await EmitObservedAsync(client, session, LifecycleStart("new-call", 4));
        else
        {
            var task = LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running);
            if (work == "independent-task")
            {
                task.Id = "independent";
                task.ToolCallId = "independent-call";
                typeof(CopilotSdkChatClient).GetMethod("RegisterSubAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(client, ["child", "independent-call", child]);
            }
            await SnapshotAsync(client, session, 4, task);
        }
        await EmitObservedAsync(client, session, new SystemNotificationEvent
        {
            Id = Guid.NewGuid(), Timestamp = LifecycleTime.AddSeconds(work == "independent-task" ? 5 : 3),
            Data = new()
            {
                Content = "not correlation",
                Kind = new SystemNotificationAgentCompleted { AgentId = "task", AgentType = "task", Status = new("completed") },
            },
        });
        Assert.False(receiver.BeginInvocation());
        Assert.Equal(AgentChatCompletionState.Running, chat.CompletionState);
    }

    [Fact]
    public async Task CopilotSdkChatClient_InterruptedReceiver_SameEpochSnapshotDoesNotReopenChannel()
    {
        var session = new Infrastructure.FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new Infrastructure.FakeCopilotClientFactory(new Infrastructure.FakeCopilotClient(session)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        await InvokeEnsureSessionAsync(client);
        var receiver = new CopilotSubAgentChatClient();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = LifecycleDefinition(), ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = receiver, ForegroundScheduler = new LifecycleInlineScheduler(),
        });
        var child = new SubAgent(new AgentSessionId(chat.AgentSessionId), chat, null);
        RegisterLifecycleChild(client, child);
        await using var parent = new LifecycleParentTurn(client, session, true);
        await EmitObservedAsync(client, session, LifecycleStart());
        await RouterBarrierAsync(client, session, parent.Pending!, 0);
        parent.Next();
        await SnapshotAsync(client, session, 1, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        var router = (CopilotSubAgentRouter)typeof(CopilotSdkChatClient)
            .GetField("currentRouter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        await router.TerminalizeRemainingChildrenAsync(new OperationCanceledException());
        await SnapshotAsync(client, session, 2, LifecycleTask(GitHub.Copilot.Rpc.TaskStatus.Running));
        Assert.True(receiver.BeginInvocation());
        await EmitObservedAsync(client, session, LifecycleFinish(epoch: 3, cancelled: true));
        Assert.True(client.IsInvocationTerminal("call"));
    }

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
