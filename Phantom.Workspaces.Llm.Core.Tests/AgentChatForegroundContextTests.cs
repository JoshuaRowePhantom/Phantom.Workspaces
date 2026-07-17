using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Concurrent;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// Enforcement tests for the AgentChat foreground-context affinity invariant (issue #909):
/// when an <see cref="SynchronizationContextTaskScheduler"/> is supplied as the foreground
/// scheduler, the <see cref="AgentChat"/> constructor (and therefore CreateAsync/InitializeAsync,
/// which follow it synchronously) must be invoked on that context and throw otherwise.
/// Plain schedulers carry no verifiable thread affinity and remain unchecked so headless
/// hosts (CLI, tests) keep working.
/// </summary>
public sealed class AgentChatForegroundContextTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static InternalCreateAgentChatRequest CreateRequest(
        TaskScheduler? foregroundScheduler,
        DeterministicTestChatClient? client = null) =>
        new()
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client ?? CreateCompletedEchoClient(),
            DisplayNameOverride = "foreground-context-test-chat",
            ForegroundScheduler = foregroundScheduler,
        };

    private static DeterministicTestChatClient CreateCompletedEchoClient()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "pong")
        {
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();
        return client;
    }

    private const string ScriptedToolsetAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": [
            { "kind": "scripted_kind", "description": "Scripted toolset" }
          ]
        }
        """;

    private static InternalCreateAgentChatRequest CreateRequestWithToolset(
        TaskScheduler? foregroundScheduler,
        AIContextProvider provider,
        DeterministicTestChatClient? client = null)
    {
        var toolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
            kind: "scripted_kind",
            createToolsetAsync: (_, _) => Task.FromResult<AIContextProvider?>(provider));
        return new()
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(ScriptedToolsetAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client ?? CreateCompletedEchoClient(),
            DisplayNameOverride = "foreground-context-toolset-chat",
            ForegroundScheduler = foregroundScheduler,
            AgentServices = new AgentServices { ToolsetFactory = toolsetFactory },
        };
    }

    [Fact]
    public async Task InitializeMcpTools_RunsRunningItemMutationsOnForegroundScheduler()
    {
        // All CreateRunningItem/UpdateRunningItem/CompleteRunningItem calls made while a toolset
        // loads must be observed on the foreground scheduler (the pump thread), not thread-pool
        // threads (issue #1068).
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        var provider = new ScriptedToolsetContextProvider(tools: [new WebSearchTool()]);

        var mutationThreads = new ConcurrentQueue<int>();
        var chat = await pump.PostAsync(() => AgentChat.CreateAsync(
            CreateRequestWithToolset(scheduler, provider),
            onConstructed: c => ((INotifyCollectionChanged)c.RunningItems).CollectionChanged +=
                (_, _) => mutationThreads.Enqueue(Environment.CurrentManagedThreadId)));
        try
        {
            Assert.NotEmpty(mutationThreads);
            Assert.All(mutationThreads, threadId => Assert.Equal(pump.ThreadId, threadId));
            Assert.Empty(chat.RunningItems);
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunningItems_AreNeverMutatedOffForegroundScheduler_DuringInit()
    {
        // With overlapping processing-loop and tool-init activity, every mutation of RunningItems
        // must occur on the foreground scheduler (issue #1068): a gated toolset load overlaps the
        // loop processing a message enqueued before initialization finished.
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);

        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedToolsetContextProvider(
            tools: [new WebSearchTool()],
            invoked: invoked,
            release: release.Task);

        var mutationThreads = new ConcurrentQueue<int>();
        AgentChat? captured = null;
        var createTask = pump.PostAsync(() => AgentChat.CreateAsync(
            CreateRequestWithToolset(scheduler, provider),
            onConstructed: c =>
            {
                captured = c;
                ((INotifyCollectionChanged)c.RunningItems).CollectionChanged +=
                    (_, _) => mutationThreads.Enqueue(Environment.CurrentManagedThreadId);
            }));

        await invoked.Task;
        var chat = captured!;
        pump.Context.Post(_ => chat.EnqueueUserMessage("ping"), null);
        await WaitForConditionAsync(
            chat.History,
            () => chat.History.Any(item => item.Role == ChatRole.Assistant),
            "queued message to be answered while tool init is gated",
            scheduler);

        release.TrySetResult();
        await createTask;
        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 0,
            "all running items to clear",
            scheduler);

        try
        {
            Assert.NotEmpty(mutationThreads);
            Assert.All(mutationThreads, threadId => Assert.Equal(pump.ThreadId, threadId));
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeMcpTools_RunningItemMutations_OccurOnForegroundScheduler()
    {
        // Every init/tool running-item create/update/complete call (session init plus each toolset
        // load) executes on the foreground scheduler, and the session step surfaces its own running
        // item (issue #1072, consistent with #1068).
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        var provider = new ScriptedToolsetContextProvider(tools: [new WebSearchTool()]);

        var mutationThreads = new ConcurrentQueue<int>();
        var seenRunningTexts = new List<string>();
        var chat = await pump.PostAsync(() => AgentChat.CreateAsync(
            CreateRequestWithToolset(scheduler, provider),
            onConstructed: c => ((INotifyCollectionChanged)c.RunningItems).CollectionChanged +=
                (_, e) =>
                {
                    mutationThreads.Enqueue(Environment.CurrentManagedThreadId);
                    if (e.NewItems is null)
                    {
                        return;
                    }

                    foreach (AgentChatRunningItem item in e.NewItems)
                    {
                        var text = item.Items.Count > 0
                            ? string.Concat(item.Items[0].Contents.OfType<TextContent>().Select(static content => content.Text))
                            : string.Empty;
                        lock (seenRunningTexts)
                        {
                            seenRunningTexts.Add(text);
                        }
                    }
                }));
        try
        {
            Assert.NotEmpty(mutationThreads);
            Assert.All(mutationThreads, threadId => Assert.Equal(pump.ThreadId, threadId));
            Assert.Contains(seenRunningTexts, text => text == "Loading session");
            Assert.Empty(chat.RunningItems);
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public void Constructor_SynchronizationContextSchedulerOffContext_ThrowsInvalidOperationException()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);

        // The test thread is not on the pump's context, so construction must fail fast.
        var exception = Assert.Throws<InvalidOperationException>(
            () => new AgentChat(CreateRequest(scheduler)));
        Assert.Contains("foreground", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_SynchronizationContextSchedulerOffContext_ThrowsInvalidOperationException()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);

        // The exact pre-#909 production shape: creation inside Task.Run with a pre-captured
        // UI scheduler. CreateAsync covers InitializeAsync, which follows the constructor
        // synchronously on the same thread.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => AgentChat.CreateAsync(CreateRequest(scheduler))));
    }

    [Fact]
    public async Task CreateAsync_OnMatchingSynchronizationContext_Succeeds()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);

        var chat = await pump.PostAsync(() => AgentChat.CreateAsync(CreateRequest(scheduler)));
        try
        {
            // The chat is fully operational: a turn completes end-to-end on the pump thread.
            var mutationThreadIds = new ConcurrentQueue<int>();
            var historyNotifications = (System.Collections.Specialized.INotifyCollectionChanged)chat.History;
            historyNotifications.CollectionChanged += (_, _) =>
                mutationThreadIds.Enqueue(Environment.CurrentManagedThreadId);

            chat.EnqueueUserMessage("ping");
            await WaitForConditionAsync(
                chat.History,
                () => chat.History.Count == 2 && chat.History[^1].Role == ChatRole.Assistant,
                "assistant response to complete",
                scheduler);

            Assert.NotEmpty(mutationThreadIds);
            Assert.All(mutationThreadIds, threadId => Assert.Equal(pump.ThreadId, threadId));
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_StartedOnForegroundScheduler_Succeeds()
    {
        // The pump thread does not install the context, so acceptance can only come from
        // TaskScheduler.Current matching the scheduler the creation was started on.
        using var pump = new SingleThreadPump(installSynchronizationContext: false);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);

        var chat = await Task.Factory.StartNew(
            () => AgentChat.CreateAsync(CreateRequest(scheduler)),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler).Unwrap();
        await chat.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_PlainSchedulerOffContext_DoesNotEnforceAffinity()
    {
        // Headless hosts (CLI, tests) pass plain schedulers, which carry no verifiable
        // thread affinity; creation from any thread must keep working.
        var chat = await Task.Run(() => AgentChat.CreateAsync(CreateRequest(TaskScheduler.Default)));
        await chat.DisposeAsync();
    }

    [Fact]
    public async Task SynchronizationContextTaskScheduler_QueueTask_ExecutesOnCapturedContext()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);

        Assert.Equal(1, scheduler.MaximumConcurrencyLevel);
        Assert.False(scheduler.IsOnSynchronizationContext);

        var (threadId, wasOnContext) = await Task.Factory.StartNew(
            () => (Environment.CurrentManagedThreadId, scheduler.IsOnSynchronizationContext),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler);

        Assert.Equal(pump.ThreadId, threadId);
        Assert.True(wasOnContext);
    }

    private const string SuspendingClientSessionId = "persisted-session";

    private static InMemoryAgentPersistenceStore CreateSeededStore(
        string sessionId,
        params ChatMessage[] messages)
    {
        var store = new InMemoryAgentPersistenceStore();
        store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent { AgentSessionId = sessionId },
            NewMessages = messages,
        }).GetAwaiter().GetResult();
        return store;
    }

    // Builds a chat-client creation path that genuinely suspends: it signals <paramref name="invoked"/>
    // and then awaits <paramref name="gate"/> before returning a client. When the gate is completed
    // from a thread-pool thread, the client-creation await in AgentChat.InitializeAsync (which uses
    // ConfigureAwait(false)) resumes off the captured foreground context, reproducing the production
    // race in issue #1098.
    private static Func<CancellationToken, Task<IChatClient>> CreateSuspendingClientFactory(
        TaskCompletionSource invoked,
        Task gate) =>
        async _ =>
        {
            invoked.TrySetResult();
            await gate.ConfigureAwait(false);
            return CreateCompletedEchoClient();
        };

    private static InternalCreateAgentChatRequest CreateSuspendingRequest(
        TaskScheduler? foregroundScheduler,
        string sessionId,
        IAgentPersistenceStore store,
        Func<CancellationToken, Task<IChatClient>> factory) =>
        new()
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson),
            ConfiguredStore = store,
            AgentSessionId = sessionId,
            DisplayNameOverride = "foreground-context-suspend-chat",
            ForegroundScheduler = foregroundScheduler,
            ChatClientFactoryOverride = factory,
            OverrideUseProvidedChatClientAsIs = true,
        };

    [Fact]
    public async Task InitializeAsync_PersistedHistoryLoad_AddsHistoryOnForegroundScheduler()
    {
        // With a suspending chat-client creation (so the post-ConfigureAwait(false) continuation
        // resumes off the pump thread), every History CollectionChanged raised while loading the
        // persisted messages must still fire on the foreground scheduler (pump thread), never a
        // thread-pool thread (issue #1098).
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        var store = CreateSeededStore(
            SuspendingClientSessionId,
            new ChatMessage(ChatRole.User, "persisted-user"),
            new ChatMessage(ChatRole.Assistant, "persisted-assistant"));

        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CreateSuspendingClientFactory(invoked, gate.Task);

        var historyThreads = new ConcurrentQueue<int>();
        var createTask = pump.PostAsync(() => AgentChat.CreateAsync(
            CreateSuspendingRequest(scheduler, SuspendingClientSessionId, store, factory),
            onConstructed: c => ((INotifyCollectionChanged)c.History).CollectionChanged +=
                (_, _) => historyThreads.Enqueue(Environment.CurrentManagedThreadId)));

        await invoked.Task;
        await Task.Run(gate.SetResult);
        var chat = await createTask;
        try
        {
            Assert.Equal(2, chat.History.Count);
            Assert.NotEmpty(historyThreads);
            Assert.All(historyThreads, threadId => Assert.Equal(pump.ThreadId, threadId));
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenClientCreationSuspends_HistoryAddDoesNotOccurOnThreadPoolThread()
    {
        // Captures the managed thread ids of init-time History.Add notifications and asserts none
        // equals the thread-pool thread that completed the client-creation gate — all are the pump
        // thread (issue #1098).
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        var store = CreateSeededStore(
            SuspendingClientSessionId,
            new ChatMessage(ChatRole.User, "persisted-user"),
            new ChatMessage(ChatRole.Assistant, "persisted-assistant"));

        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CreateSuspendingClientFactory(invoked, gate.Task);

        var historyThreads = new ConcurrentQueue<int>();
        var createTask = pump.PostAsync(() => AgentChat.CreateAsync(
            CreateSuspendingRequest(scheduler, SuspendingClientSessionId, store, factory),
            onConstructed: c => ((INotifyCollectionChanged)c.History).CollectionChanged +=
                (_, _) => historyThreads.Enqueue(Environment.CurrentManagedThreadId)));

        await invoked.Task;
        var gateThreadId = await Task.Run(() =>
        {
            gate.SetResult();
            return Environment.CurrentManagedThreadId;
        });
        var chat = await createTask;
        try
        {
            Assert.NotEmpty(historyThreads);
            Assert.DoesNotContain(gateThreadId, historyThreads);
            Assert.All(historyThreads, threadId => Assert.Equal(pump.ThreadId, threadId));
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_SessionInitRunningItem_MutatesOnForegroundScheduler_WhenClientCreationSuspends()
    {
        // The session-init running item ("Loading session") create/complete mutations must occur on
        // the foreground scheduler even when the client-creation await suspends and its continuation
        // resumes on a thread-pool thread (issues #1098 / #1072).
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        var store = CreateSeededStore(
            SuspendingClientSessionId,
            new ChatMessage(ChatRole.User, "persisted-user"));

        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CreateSuspendingClientFactory(invoked, gate.Task);

        var mutationThreads = new ConcurrentQueue<int>();
        var seenRunningTexts = new List<string>();
        var createTask = pump.PostAsync(() => AgentChat.CreateAsync(
            CreateSuspendingRequest(scheduler, SuspendingClientSessionId, store, factory),
            onConstructed: c => ((INotifyCollectionChanged)c.RunningItems).CollectionChanged +=
                (_, e) =>
                {
                    mutationThreads.Enqueue(Environment.CurrentManagedThreadId);
                    if (e.NewItems is null)
                    {
                        return;
                    }

                    foreach (AgentChatRunningItem item in e.NewItems)
                    {
                        var text = item.Items.Count > 0
                            ? string.Concat(item.Items[0].Contents.OfType<TextContent>().Select(static content => content.Text))
                            : string.Empty;
                        lock (seenRunningTexts)
                        {
                            seenRunningTexts.Add(text);
                        }
                    }
                }));

        await invoked.Task;
        await Task.Run(gate.SetResult);
        var chat = await createTask;
        try
        {
            Assert.NotEmpty(mutationThreads);
            Assert.All(mutationThreads, threadId => Assert.Equal(pump.ThreadId, threadId));
            Assert.Contains(seenRunningTexts, text => text == "Loading session");
            Assert.Empty(chat.RunningItems);
        }
        finally
        {
            await chat.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeAsync_PersistedHistory_NotVisibleUntilForegroundSchedulerPumped()
    {
        // With a foreground SynchronizationContextTaskScheduler whose context only executes when
        // externally pumped, the persisted History load must not be observed until the context is
        // drained — proving the load is marshalled onto the foreground scheduler rather than applied
        // on the off-thread post-ConfigureAwait(false) continuation (issue #1098).
        var context = new DeferredSynchronizationContext();
        var scheduler = new SynchronizationContextTaskScheduler(context);
        var store = CreateSeededStore(
            SuspendingClientSessionId,
            new ChatMessage(ChatRole.User, "persisted-user"),
            new ChatMessage(ChatRole.Assistant, "persisted-assistant"));

        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CreateSuspendingClientFactory(invoked, gate.Task);

        AgentChat? captured = null;
        var historyThreads = new ConcurrentQueue<int>();
        var createTask = CreateOnContextAsync(
            context,
            CreateSuspendingRequest(scheduler, SuspendingClientSessionId, store, factory),
            onConstructed: c =>
            {
                captured = c;
                ((INotifyCollectionChanged)c.History).CollectionChanged +=
                    (_, _) => historyThreads.Enqueue(Environment.CurrentManagedThreadId);
            });

        await invoked.Task;
        await Task.Run(gate.SetResult);

        // Wait until the off-thread continuation has either queued foreground work (marshalled fix)
        // or completed initialization inline (unmarshalled bug).
        await Task.WhenAny(createTask, context.WorkQueued);

        var chat = captured!;
        Assert.False(createTask.IsCompleted);
        Assert.Empty(chat.History);
        Assert.False(chat.HistoryPopulated.IsCompleted);

        var drainThreadId = await DrainUntilCompleteAsync(context, createTask);
        try
        {
            Assert.Equal(2, chat.History.Count);
            Assert.NotEmpty(historyThreads);
            Assert.All(historyThreads, threadId => Assert.Equal(drainThreadId, threadId));
        }
        finally
        {
            await DisposeWithDrainAsync(chat, context);
        }
    }

    [Fact]
    public async Task InitializeAsync_HistoryPopulated_CompletesOnForegroundContext()
    {
        // historyPopulated.TrySetResult() must run on the foreground scheduler: with a foreground
        // SynchronizationContextTaskScheduler whose context only executes when externally pumped,
        // HistoryPopulated must remain incomplete until the context is drained (issue #1098; #1009
        // consumers await HistoryPopulated).
        var context = new DeferredSynchronizationContext();
        var scheduler = new SynchronizationContextTaskScheduler(context);
        var store = CreateSeededStore(
            SuspendingClientSessionId,
            new ChatMessage(ChatRole.User, "persisted-user"));

        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CreateSuspendingClientFactory(invoked, gate.Task);

        AgentChat? captured = null;
        var createTask = CreateOnContextAsync(
            context,
            CreateSuspendingRequest(scheduler, SuspendingClientSessionId, store, factory),
            onConstructed: c => captured = c);

        await invoked.Task;
        await Task.Run(gate.SetResult);
        await Task.WhenAny(createTask, context.WorkQueued);

        var chat = captured!;
        Assert.False(chat.HistoryPopulated.IsCompleted);

        await DrainUntilCompleteAsync(context, createTask);
        try
        {
            Assert.True(chat.HistoryPopulated.IsCompleted);
        }
        finally
        {
            await DisposeWithDrainAsync(chat, context);
        }
    }

    // Constructs the AgentChat on <paramref name="context"/> (installed as the current
    // SynchronizationContext) so the #909 foreground-affinity verification in the constructor
    // passes, then restores the previous context. CreateAsync runs synchronously up to the
    // suspending client-creation gate and returns the still-running initialization task.
    private static Task<AgentChat> CreateOnContextAsync(
        DeferredSynchronizationContext context,
        InternalCreateAgentChatRequest request,
        Action<AgentChat> onConstructed)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return AgentChat.CreateAsync(request, onConstructed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // Drains <paramref name="context"/> on a single dedicated thread until <paramref name="task"/>
    // completes, returning that thread's managed id so callers can assert the marshalled foreground
    // work ran on the pump thread.
    private static async Task<int> DrainUntilCompleteAsync(DeferredSynchronizationContext context, Task task)
    {
        var drainThreadId = 0;
        await Task.Run(() =>
        {
            drainThreadId = Environment.CurrentManagedThreadId;
            while (!task.IsCompleted)
            {
                if (context.DrainAll() == 0)
                {
                    Thread.Yield();
                }
            }
        });
        await task;
        return drainThreadId;
    }

    private static async Task DisposeWithDrainAsync(AgentChat chat, DeferredSynchronizationContext context)
    {
        var disposeTask = chat.DisposeAsync().AsTask();
        await Task.Run(() =>
        {
            while (!disposeTask.IsCompleted)
            {
                if (context.DrainAll() == 0)
                {
                    Thread.Yield();
                }
            }
        });
        await disposeTask;
    }

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description,
        TaskScheduler foregroundScheduler)
    {
        // condition() enumerates the live collection, which is mutated on foregroundScheduler (the
        // pump thread). Evaluating it on the test thread races those mutations and intermittently
        // throws "Collection was modified; enumeration operation may not execute." Evaluate the
        // condition on foregroundScheduler instead so every read is serialized with the writes
        // (issue #1100).
        Task<bool> EvaluateOnForegroundAsync() =>
            Task.Factory.StartNew(
                condition,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                foregroundScheduler);

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Raised synchronously by the mutation on foregroundScheduler's thread, so condition()
            // reads the collection here without racing a concurrent write.
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (await EvaluateOnForegroundAsync())
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var registration = timeout.Token.Register(
                () => signal.TrySetException(new TimeoutException($"Timed out waiting for {description}.")));
            await signal.Task;
        }
        finally
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }
    }

    /// <summary>
    /// A single dedicated thread that sequentially processes callbacks posted to
    /// <see cref="Context"/>. When <c>installSynchronizationContext</c> is set, the pump thread
    /// installs the context as <see cref="SynchronizationContext.Current"/> (mirroring a UI
    /// thread); otherwise it leaves it uninstalled so tests can isolate the
    /// <see cref="TaskScheduler.Current"/> acceptance path.
    /// </summary>
    internal sealed class SingleThreadPump : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
        private readonly Thread thread;

        public SingleThreadPump(bool installSynchronizationContext)
        {
            this.Context = new PumpSynchronizationContext(this.queue);
            this.thread = new Thread(() =>
            {
                if (installSynchronizationContext)
                {
                    SynchronizationContext.SetSynchronizationContext(this.Context);
                }

                foreach (var (callback, state) in this.queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            })
            {
                IsBackground = true,
                Name = "test-foreground-context-pump",
            };
            this.thread.Start();
        }

        public SynchronizationContext Context { get; }

        public int ThreadId => this.thread.ManagedThreadId;

        /// <summary>
        /// Posts <paramref name="work"/> to the pump thread and completes when the task
        /// returned by <paramref name="work"/> completes.
        /// </summary>
        public Task<T> PostAsync<T>(Func<Task<T>> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.Context.Post(
                _ => work().ContinueWith(
                    task =>
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            completion.SetResult(task.Result);
                        }
                        else if (task.IsFaulted)
                        {
                            completion.SetException(
                                task.Exception!.InnerExceptions.Count == 1
                                    ? task.Exception.InnerException!
                                    : task.Exception);
                        }
                        else
                        {
                            completion.SetCanceled();
                        }
                    },
                    TaskScheduler.Default),
                null);
            return completion.Task;
        }

        public void Dispose() => this.queue.CompleteAdding();

        private sealed class PumpSynchronizationContext(
            BlockingCollection<(SendOrPostCallback Callback, object? State)> queue) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

            public override void Send(SendOrPostCallback d, object? state) =>
                throw new NotSupportedException("Synchronous Send is not supported by the test pump.");
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that never runs posted callbacks on its own: they run
    /// only when a test calls <see cref="DrainAll"/>. Wrapped in a
    /// <see cref="SynchronizationContextTaskScheduler"/> it lets a test construct the chat on this
    /// context (satisfying the #909 foreground-affinity check) and then control exactly when the
    /// marshalled foreground work runs — proving that init-time history/session mutations are queued
    /// onto the foreground scheduler rather than applied on the off-thread post-ConfigureAwait(false)
    /// continuation (issue #1098). <see cref="DrainAll"/> installs this context while executing so
    /// continuations posted by awaits inside the marshalled work re-enqueue here.
    /// </summary>
    internal sealed class DeferredSynchronizationContext : SynchronizationContext
    {
        private readonly object gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queued = new();
        private readonly TaskCompletionSource workQueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WorkQueued => this.workQueued.Task;

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (this.gate)
            {
                this.queued.Enqueue((d, state));
            }

            this.workQueued.TrySetResult();
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Synchronous Send is not supported by the deferred sync context.");

        public int DrainAll()
        {
            var executed = 0;
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) next;
                    lock (this.gate)
                    {
                        if (this.queued.Count == 0)
                        {
                            break;
                        }

                        next = this.queued.Dequeue();
                    }

                    next.Callback(next.State);
                    executed++;
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }

            return executed;
        }
    }
}
