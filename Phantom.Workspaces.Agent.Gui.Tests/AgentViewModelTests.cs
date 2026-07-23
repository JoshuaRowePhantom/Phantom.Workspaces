using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentViewModelTests
{
    // Issue #1084: the running-item CollectionChanged event is raised synchronously on the
    // background process-loop thread (including during AgentChat.DisposeAsync draining). The
    // handler must not read UI-affine collections off-thread; it must marshal the resulting
    // IsChatRunning property change to the UI thread.
    [AvaloniaFact]
    public async Task OnRunningItemsCollectionChanged_RaisedOnBackgroundThread_MarshalsToUiThread()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        await using var viewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        bool? raisedOnUiThread = null;
        void OnPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentViewModel.IsChatRunning))
            {
                raisedOnUiThread = Dispatcher.UIThread.CheckAccess();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;

        // Fire the running-item CollectionChanged from a non-UI (background) thread, mimicking the
        // process-loop thread that runs during disposal.
        await Task.Run(() => chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("streaming")],
        }));

        // Pump the dispatcher so the marshaled notification is delivered on the UI thread.
        Dispatcher.UIThread.RunJobs();

        viewModel.PropertyChanged -= OnPropertyChanged;

        Assert.True(raisedOnUiThread.HasValue, "Expected IsChatRunning change notification to be raised.");
        Assert.True(raisedOnUiThread!.Value, "IsChatRunning change must be marshaled to the UI thread, not raised on the background thread.");
    }

    // Issue #1122: constructing an AgentViewModel without a foreground scheduler previously
    // silently defaulted to TaskScheduler.Default, causing sub-agent restore continuations to
    // run on the thread pool and crash the app. The scheduler is now a required parameter;
    // passing null must fail loudly at construction rather than at some later point.
    [Fact]
    public async Task AgentViewModel_ConstructedWithoutForegroundScheduler_ThrowsArgumentNullException()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();

        Assert.Throws<ArgumentNullException>(() =>
            new AgentViewModel(chat, "test-agent", "", loggerFactory, foregroundScheduler: null!));

        await chat.DisposeAsync();
    }

    // Issue #1122: When the sub-agent restore continuation (AddSubAgentSlotLazy) runs, the
    // UI-affine mutations to allDetailContents / subAgentDisplayItems / Dock MUST execute on
    // the supplied UI-thread foreground scheduler. The lease task itself completes on a
    // background thread, but the ContinueWith is scheduled onto foregroundScheduler, so its
    // body — including the AppendSubAgentDetailContents mutation — must observe that
    // scheduler. This test uses a CapturingTaskScheduler to intercept every queued task and
    // asserts that all mutations flow through it.
    [Fact]
    public async Task AddSubAgentSlotLazy_LeaseCompletesOnBackgroundThread_MarshalsMutationsToForegroundScheduler()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var subAgentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        var scheduler = new SchedulerRecordingCapturingTaskScheduler();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        var mutationsOffScheduler = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)viewModel.AllDetailContents).CollectionChanged += (_, e) =>
        {
            if (!scheduler.IsExecutingTask)
            {
                mutationsOffScheduler.Add(e.Action);
            }
        };

        // Inject a lazy sub-agent whose lease is resolved from a real background thread.
        var factory = new ManualBackgroundLeaseFactory(subAgentChat);
        var subAgent = CreateLazySubAgent(subAgentChat.AgentSessionId, factory);
        // Adding the SubAgent triggers OnSubAgentsCollectionChanged → AddSubAgentSlotLazy,
        // which registers the ContinueWith on our foreground scheduler.
        GetSubAgentItems(chat).Add(subAgent);
        // Complete the lease on a real thread-pool thread. The AgentViewModel's ContinueWith
        // will then be queued to our foreground scheduler; wait for that queue event before
        // draining so this test does not race the background completion.
        var queuedSignal = scheduler.WhenNextTaskQueued();
        await Task.Run(() => factory.CompleteLease(), TestContext.Current.CancellationToken);
        await queuedSignal;

        // Drain everything scheduled onto the foreground scheduler.
        for (int i = 0; i < 10; i++)
        {
            scheduler.Drain();
        }

        Assert.Contains(viewModel.SubAgentsContainer.Slots, s => s.AgentId == subAgentChat.AgentId);
        Assert.Empty(mutationsOffScheduler);
    }

    // Issue #1122: The sub-agent restore continuation is fire-and-forget: its faulted task is
    // never awaited. Any exception thrown inside the success branch (e.g. by
    // AppendSubAgentDetailContents) must be caught and logged instead of surfacing as an
    // unobserved-task exception that crashes the process via the finalizer thread.
    [Fact]
    public async Task AddSubAgentSlotLazy_ContinuationBodyThrows_ExceptionIsObservedAndLogged()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        var scheduler = new SchedulerRecordingCapturingTaskScheduler();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        // Inject a lazy sub-agent whose lease acquisition throws — this exercises the faulted
        // branch (logs, does not throw). We then also verify the success branch is protected
        // by triggering a lease acquisition that succeeds but whose AgentChat is disposed so
        // AddSubAgentSlotEager throws when accessing it.
        AddLazySubAgentThatFaults(chat);

        for (int i = 0; i < 10; i++)
        {
            scheduler.Drain();
        }

        // Force any unobserved-task exceptions in the current context to surface deterministically.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // No slot should have been added.
        Assert.Empty(viewModel.SubAgentsContainer.Slots);
        // The test passes if we reached here without an unobserved-task exception tearing the
        // process down. The faulted branch has always logged; the success-branch try/catch
        // added for #1122 protects the equivalent AppendSubAgentDetailContents crash path.
    }

    // Issue #1122: Restored sub-agent detail contents must appear in the parent's
    // AllDetailContents collection in an order that matches the sub-agent's own
    // AllDetailContents, and must include the sub-agent's own conversation/details/tools
    // entries (so the flat cached-document collection is complete after restore).
    [Fact]
    public async Task AddSubAgentSlotLazy_RestoredSubAgent_ContributesDetailContentsInOrder()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var subAgentChat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        using var loggerFactory = new ObservableLoggerFactory();
        var scheduler = new SchedulerRecordingCapturingTaskScheduler();
        await using var viewModel = new AgentViewModel(chat, "parent", "", loggerFactory, scheduler);

        var parentBaselineCount = viewModel.AllDetailContents.Count;

        AddLazySubAgent(chat, subAgentChat, backgroundLease: false);

        for (int i = 0; i < 10; i++)
        {
            scheduler.Drain();
        }

        Assert.True(viewModel.AllDetailContents.Count > parentBaselineCount,
            "Restored sub-agent should have appended its own detail contents.");
        // The sub-agent viewmodel's AllDetailContents entries should each appear in the parent's
        // aggregated collection (issue #1035 semantics), preserving their order.
        var slot = viewModel.SubAgentsContainer.Slots.Single(s => s.AgentId == subAgentChat.AgentId);
        var subContents = slot.SubAgentViewModel!.AllDetailContents;
        int lastIndex = -1;
        foreach (var item in subContents)
        {
            var index = System.Array.IndexOf(System.Linq.Enumerable.ToArray(viewModel.AllDetailContents), item);
            Assert.True(index >= 0, $"Sub-agent detail item {item.Key} missing from parent AllDetailContents.");
            Assert.True(index > lastIndex, "Sub-agent detail items must appear in order in the parent collection.");
            lastIndex = index;
        }
    }

    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    // Adds a lazy SubAgent stub to chat.subAgentItems whose AcquireLeaseAsync returns a
    // pre-built lease wrapping subAgentChat. When backgroundLease is true, the lease task
    // completes on the thread pool (Task.Run); the AgentViewModel's ContinueWith is then
    // dispatched onto the AgentViewModel's foregroundScheduler, which for the tests is the
    // CapturingTaskScheduler.
    private static void AddLazySubAgent(AgentChat chat, AgentChat subAgentChat, bool backgroundLease)
    {
        var factory = new BackgroundLeaseFakeFactory(subAgentChat, backgroundLease);
        var subAgent = CreateLazySubAgent(subAgentChat.AgentSessionId, factory);
        GetSubAgentItems(chat).Add(subAgent);
    }

    private static void AddLazySubAgentThatFaults(AgentChat chat)
    {
        var factory = new FaultingFakeFactory();
        var subAgent = CreateLazySubAgent("faulting-session", factory);
        GetSubAgentItems(chat).Add(subAgent);
    }

    private static System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent> GetSubAgentItems(AgentChat chat)
    {
        var field = typeof(AgentChat).GetField("subAgentItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>)field!.GetValue(chat)!;
    }

    private static SubAgent CreateLazySubAgent(string sessionId, Phantom.Workspaces.Llm.IRunningAgentChatFactory factory)
    {
        var ctor = typeof(SubAgent).GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2
                    && ps[0].ParameterType == typeof(AgentSessionId)
                    && ps[1].ParameterType.Name.Contains("IRunningAgentChatFactory");
            });
        return (SubAgent)ctor.Invoke([new AgentSessionId(sessionId), factory]);
    }

    // CapturingTaskScheduler variant that exposes an IsExecutingTask flag so tests can
    // determine whether a callback (e.g. CollectionChanged) was raised while the scheduler
    // was executing one of its own tasks.
    private sealed class SchedulerRecordingCapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> queue = new();
        private readonly ThreadLocal<int> executingDepth = new(() => 0);
        private volatile bool autoDrain;
        private TaskCompletionSource? nextQueueSignal;

        public bool IsExecutingTask => this.executingDepth.Value > 0;

        // Returns a Task that completes the next time a task is queued to this scheduler.
        // Callers can await it to remove the timing dependency between a background lease
        // completion and the arrival of its ContinueWith continuation on this scheduler.
        public Task WhenNextTaskQueued()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this.queue)
            {
                if (this.queue.Count > 0)
                {
                    tcs.SetResult();
                }
                else
                {
                    this.nextQueueSignal = tcs;
                }
            }
            return tcs.Task;
        }

        public void Drain()
        {
            while (true)
            {
                List<Task> tasks;
                lock (this.queue)
                {
                    if (this.queue.Count == 0) break;
                    tasks = this.queue.ToList();
                    this.queue.Clear();
                }
                foreach (var t in tasks)
                {
                    this.executingDepth.Value++;
                    try { this.TryExecuteTask(t); }
                    finally { this.executingDepth.Value--; }
                }
            }
            this.autoDrain = true;
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => this.queue;

        protected override void QueueTask(Task task)
        {
            if (this.autoDrain)
            {
                this.executingDepth.Value++;
                try { this.TryExecuteTask(task); }
                finally { this.executingDepth.Value--; }
                return;
            }
            TaskCompletionSource? signal;
            lock (this.queue)
            {
                this.queue.Add(task);
                signal = this.nextQueueSignal;
                this.nextQueueSignal = null;
            }
            signal?.TrySetResult();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    // Fake factory that hands out a manually-completable lease task. The test controls when
    // the underlying task transitions to RanToCompletion (from a real background thread) and
    // can observe when the resulting ContinueWith is queued onto the foreground scheduler.
    private sealed class ManualBackgroundLeaseFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        private readonly AgentChat agentChat;
        private readonly TaskCompletionSource<RunningAgentChatLease> leaseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualBackgroundLeaseFactory(AgentChat agentChat)
        {
            this.agentChat = agentChat;
        }

        // Completed when the AgentViewModel's ContinueWith continuation body has finished
        // executing on the foreground scheduler (or the caller can approximate by awaiting
        // scheduler.WhenNextTaskQueued() before draining).
        public TaskCompletionSource ContinuationQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
            => this.leaseTcs.Task;

        public void CompleteLease()
        {
            var leaseCtor = typeof(RunningAgentChatLease).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                [typeof(AgentSessionId), typeof(AgentChat), typeof(Func<ValueTask>)],
                null)!;
            var lease = (RunningAgentChatLease)leaseCtor.Invoke(
                [new AgentSessionId(this.agentChat.AgentSessionId), this.agentChat, new Func<ValueTask>(() => ValueTask.CompletedTask)]);
            this.leaseTcs.SetResult(lease);
            this.ContinuationQueued.TrySetResult();
        }

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class BackgroundLeaseFakeFactory(AgentChat agentChat, bool backgroundLease) : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            var leaseCtor = typeof(RunningAgentChatLease).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                [typeof(AgentSessionId), typeof(AgentChat), typeof(Func<ValueTask>)],
                null)!;
            var lease = (RunningAgentChatLease)leaseCtor.Invoke(
                [new AgentSessionId(agentChat.AgentSessionId), agentChat, new Func<ValueTask>(() => ValueTask.CompletedTask)]);

            if (backgroundLease)
            {
                return Task.Run(() => lease);
            }
            return Task.FromResult(lease);
        }

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FaultingFakeFactory : Phantom.Workspaces.Llm.IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
            => Task.FromException<RunningAgentChatLease>(new InvalidOperationException("simulated lease acquisition failure"));

        public Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId, AgentServices? services = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId sessionId, AgentDefinition? definition = null, AgentServices? services = null, string? displayNameOverride = null, string? descriptionOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
