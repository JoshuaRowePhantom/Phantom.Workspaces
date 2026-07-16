using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
            "queued message to be answered while tool init is gated");

        release.TrySetResult();
        await createTask;
        await WaitForConditionAsync(
            chat.RunningItems,
            () => chat.RunningItems.Count == 0,
            "all running items to clear");

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
                "assistant response to complete");

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

    private static async Task WaitForConditionAsync(
        System.Collections.Specialized.INotifyCollectionChanged collection,
        Func<bool> condition,
        string description)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        }

        collection.CollectionChanged += OnCollectionChanged;
        try
        {
            if (condition())
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
}
