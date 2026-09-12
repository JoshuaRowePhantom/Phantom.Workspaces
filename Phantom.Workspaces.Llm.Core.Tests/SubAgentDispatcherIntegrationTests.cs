using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// End-to-end tests for <see cref="SubAgentDispatcherChatClient"/> wired against a real
/// <see cref="InMemoryDataAccessLayer"/> and echo-backed sub-agent definitions. Exercises
/// sub-agent creation from named definitions, routing (explicit id, most-recent, fuzzy),
/// idle-detection, output streaming, interrupt propagation, and persistence restore.
/// </summary>
public sealed class SubAgentDispatcherIntegrationTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentDefinition EchoAgentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);

    private static AgentDefinitionTool Tool(string name) => new()
    {
        Name = name,
        Description = $"Echo agent '{name}'",
        Definition = EchoAgentDefinition,
    };

    private static SubAgentDispatcherOptions CreateOptions() => new()
    {
        AgentDefinitionTools = [Tool("default"), Tool("foo"), Tool("bar")],
    };

    private static async Task<List<ChatResponseUpdate>> DrainAsync(
        SubAgentDispatcherChatClient client,
        string message,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        var messages = new List<ChatMessage> { new(ChatRole.User, message) };
        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string AllText(IEnumerable<ChatResponseUpdate> updates) =>
        string.Join("", updates.Select(u => u.Text ?? string.Empty));

    [Fact]
    public async Task CreateTwoSubAgents_RouteToEach_StreamsAcksAndEcho()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new EchoAgentChatFactory();
        var dispatcherName = new EntityName("dispatchers", "integration");

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherName,
            CreateOptions());

        // Create the first sub-agent from the "foo" definition with an explicit id.
        var fooUpdates = await DrainAsync(client, "new(foo alpha): hello from foo", timeout.Token);
        Assert.Contains(fooUpdates, u => u.Text?.Contains("Sending") == true && u.Text.Contains("alpha"));
        Assert.Contains(fooUpdates, u => u.Text?.Contains("Created sub-agent \"alpha\"") == true);
        Assert.Contains("hello from foo", AllText(fooUpdates));

        // Create a second sub-agent from the "bar" definition.
        var barUpdates = await DrainAsync(client, "new(bar beta): hello from bar", timeout.Token);
        Assert.Contains(barUpdates, u => u.Text?.Contains("Created sub-agent \"beta\"") == true);
        Assert.Contains("hello from bar", AllText(barUpdates));

        Assert.Equal(2, client.ActiveSubAgents.Count);
        Assert.Contains(client.ActiveSubAgents, s => s.Id == "alpha");
        Assert.Contains(client.ActiveSubAgents, s => s.Id == "beta");

        // Route explicitly to alpha.
        var routeAlpha = await DrainAsync(client, "alpha: follow up to alpha", timeout.Token);
        Assert.Contains("follow up to alpha", AllText(routeAlpha));

        // Route to the most-recently-dispatched sub-agent (alpha) via bare colon.
        var routeMostRecent = await DrainAsync(client, ": bare colon message", timeout.Token);
        Assert.Contains("bare colon message", AllText(routeMostRecent));

        // Two distinct sessions were created (one per sub-agent).
        Assert.Equal(2, factory.Leases.Count);

        client.Dispose();
    }

    [Fact]
    public async Task Dispatch_CompletesOnlyAfterSubAgentIsIdle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new EchoAgentChatFactory();
        var dispatcherName = new EntityName("dispatchers", "idle");

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherName,
            CreateOptions());

        await DrainAsync(client, "new: idle please", timeout.Token);

        // By the time the streaming response has completed, the sub-agent must be idle.
        var lease = factory.Leases.Values.Single();
        Assert.Empty(lease.LocalAgentChat.RunningItems);
        Assert.True(lease.LocalAgentChat.History.Count > 0);

        client.Dispose();
    }

    [Fact]
    public async Task FuzzyRoute_RoutesToTheClosestSubAgent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new EchoAgentChatFactory();
        var dispatcherName = new EntityName("dispatchers", "fuzzy");

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherName,
            CreateOptions());

        await DrainAsync(client, "new(foo databaseagent): investigate the database migration", timeout.Token);
        await DrainAsync(client, "new(bar uiagent): polish the user interface layout", timeout.Token);

        // A fuzzy token that does not exactly match any id should still route to a sub-agent
        // and produce echoed output rather than a "not found" error.
        var fuzzy = await DrainAsync(client, "database: more work on migrations", timeout.Token);
        var text = AllText(fuzzy);
        Assert.DoesNotContain("not found", text);
        Assert.Contains("more work on migrations", text);

        client.Dispose();
    }

    [Fact]
    public async Task Cancellation_BeforeCreatedAcknowledgement_YieldsOneInterruptedWithoutDispatching()
    {
        await using var scenario = new ControlledDispatchScenario("cancel-before-created");

        var sending = await scenario.ReadRequiredAsync();
        Assert.Contains("Sending", sending.Text);

        scenario.Cancel();

        var interrupted = await scenario.ReadRequiredAsync();
        Assert.Equal("Interrupted.\n", interrupted.Text);
        Assert.False(await scenario.Enumerator.MoveNextAsync());
        Assert.Empty(scenario.Client.ActiveSubAgents);
        Assert.Empty(scenario.Factory.Leases.Values.Single().AgentChat.RunningItems);
    }

    [Fact]
    public async Task Cancellation_AfterDispatchBeforeRunning_WaitsForActiveItemAndOwnsItsCleanup()
    {
        await using var scenario = new ControlledDispatchScenario(
            "cancel-after-dispatch-before-running",
            pauseProcessing: true);

        var sending = await scenario.ReadRequiredAsync();
        Assert.Contains("Sending", sending.Text);
        var created = await scenario.ReadRequiredAsync();
        Assert.Contains("Created sub-agent", created.Text);

        var lease = scenario.Factory.Leases.Values.Single();
        var runningCounts = new List<int>();
        void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => runningCounts.Add(lease.AgentChat.RunningItems.Count);
        ((INotifyCollectionChanged)lease.AgentChat.RunningItems).CollectionChanged += OnRunningItemsChanged;

        try
        {
            Assert.Single(scenario.Client.ActiveSubAgents);
            Assert.Empty(lease.AgentChat.RunningItems);
            Assert.True(scenario.HasPausedProcessingWork);

            var interruptedMove = scenario.Enumerator.MoveNextAsync().AsTask();
            scenario.Cancel();

            Assert.False(interruptedMove.IsCompleted);
            Assert.Empty(lease.AgentChat.RunningItems);

            scenario.ReleasePausedProcessing();

            Assert.True(await interruptedMove);
            Assert.Equal("Interrupted.\n", scenario.Enumerator.Current.Text);
            Assert.False(await scenario.Enumerator.MoveNextAsync());

            Assert.Equal([1, 0], runningCounts);
            Assert.Empty(lease.AgentChat.RunningItems);
            Assert.False(scenario.HasPausedProcessingWork);

            var historyText = lease.AgentChat.History
                .SelectMany(item => item.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text)
                .ToArray();
            Assert.Single(historyText, text => text == "Interrupted by user.");
            Assert.DoesNotContain(historyText, text => text == "completed normally");
        }
        finally
        {
            ((INotifyCollectionChanged)lease.AgentChat.RunningItems).CollectionChanged -= OnRunningItemsChanged;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_WhileCreatedAcknowledgementIsYielded_WinsOverIdleInEitherOrder(
        bool completeBeforeCancel)
    {
        await using var scenario = new ControlledDispatchScenario(
            completeBeforeCancel ? "idle-then-cancel-before-ack" : "cancel-then-idle-before-ack");

        await scenario.ReadThroughCreatedAsync();
        await scenario.WaitForRunningAsync();

        if (completeBeforeCancel)
        {
            scenario.Stream.Complete();
            await scenario.WaitForIdleAsync();
            scenario.Cancel();
        }
        else
        {
            scenario.Cancel();
            scenario.Stream.Complete();
        }

        var updates = await scenario.DrainAsync();

        Assert.Equal(["Interrupted.\n"], updates.Select(update => update.Text));
        Assert.Empty(scenario.Factory.Leases.Values.Single().AgentChat.RunningItems);
    }

    [Fact]
    public async Task Completion_AfterCreatedAcknowledgement_StreamsOutputWithoutInterrupted()
    {
        await using var scenario = new ControlledDispatchScenario("normal-completion");

        await scenario.ReadThroughCreatedAsync();
        await scenario.WaitForRunningAsync();

        var pendingUpdate = scenario.Enumerator.MoveNextAsync().AsTask();
        scenario.Stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "completed normally"));
        scenario.Stream.Complete();

        Assert.True(await pendingUpdate);
        var updates = new List<ChatResponseUpdate> { scenario.Enumerator.Current };
        updates.AddRange(await scenario.DrainAsync());

        Assert.Contains(updates, update => update.Text == "completed normally");
        Assert.DoesNotContain(updates, update => update.Text == "Interrupted.\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndIdle_AfterCreatedAcknowledgement_FirstTerminalSignalWins(
        bool completeBeforeCancel)
    {
        await using var scenario = new ControlledDispatchScenario(
            completeBeforeCancel ? "idle-then-cancel-after-ack" : "cancel-then-idle-after-ack");

        await scenario.ReadThroughCreatedAsync();
        await scenario.WaitForRunningAsync();

        var pendingUpdate = scenario.Enumerator.MoveNextAsync().AsTask();
        if (completeBeforeCancel)
        {
            scenario.Stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "idle won"));
            scenario.Stream.Complete();
            await scenario.WaitForIdleAsync();
            scenario.Cancel();
        }
        else
        {
            scenario.Cancel();
            scenario.Stream.Complete();
        }

        Assert.True(await pendingUpdate);
        var updates = new List<ChatResponseUpdate> { scenario.Enumerator.Current };
        updates.AddRange(await scenario.DrainAsync());

        if (completeBeforeCancel)
        {
            Assert.Contains(updates, update => update.Text == "idle won");
            Assert.DoesNotContain(updates, update => update.Text == "Interrupted.\n");
        }
        else
        {
            Assert.Equal(["Interrupted.\n"], updates.Select(update => update.Text));
        }
    }

    [Fact]
    public async Task DisposingCreatedDispatch_UnsubscribesCancellationWithoutInterruptingAgent()
    {
        await using var scenario = new ControlledDispatchScenario("dispose-created");

        await scenario.ReadThroughCreatedAsync();
        await scenario.WaitForRunningAsync();
        await scenario.DisposeEnumeratorAsync();

        scenario.Cancel();
        scenario.Stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "finished after disposal"));
        scenario.Stream.Complete();
        await scenario.WaitForIdleAsync();

        var historyText = string.Join(
            "",
            scenario.Factory.Leases.Values.Single().AgentChat.History
                .SelectMany(item => item.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text));
        Assert.Contains("finished after disposal", historyText);
        Assert.DoesNotContain("Interrupted by user.", historyText);
    }

    [Fact]
    public async Task Persistence_RestoreAcrossInstances_RebuildsSubAgents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = new EchoAgentChatFactory();
        var dispatcherName = new EntityName("dispatchers", "persistent");

        var first = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherName,
            CreateOptions());

        await DrainAsync(first, "new(foo one): first task", timeout.Token);
        await DrainAsync(first, "new(bar two): second task", timeout.Token);
        first.Dispose();

        // A fresh dispatcher instance restores the persisted sub-agents from the shared DAL.
        var second = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherName,
            CreateOptions());

        await second.RestoreSubAgentsAsync(timeout.Token);

        var restored = second.ActiveSubAgents.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(["one", "two"], restored);

        // The restored dispatcher can route to a restored sub-agent.
        var route = await DrainAsync(second, "one: after restart", timeout.Token);
        Assert.Contains("after restart", AllText(route));

        second.Dispose();
    }

    /// <summary>An echo-backed factory that creates real AgentChats for sub-agent sessions.</summary>
    private sealed class EchoAgentChatFactory : IRunningAgentChatFactory
    {
        public Dictionary<AgentSessionId, RunningAgentChatLease> Leases { get; } = new();
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public async Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            if (Leases.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }

            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = definition ?? EchoAgentDefinition,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                DisplayNameOverride = displayNameOverride ?? "sub-agent",
                DescriptionOverride = descriptionOverride,
            });

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            Leases[sessionId] = lease;
            return lease;
        }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => GetOrCreateAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
            => GetOrCreateAsync(sessionId, definition, services, displayNameOverride, descriptionOverride, ct: ct);
    }

    /// <summary>An echo factory whose chats use a controllable client for the interrupt test.</summary>
    private sealed class ControllableEchoFactory : IRunningAgentChatFactory
    {
        private readonly DeterministicTestChatClient _chatClient;
        private readonly ManuallyDrivenTaskScheduler? _foregroundScheduler;

        public ControllableEchoFactory(
            DeterministicTestChatClient chatClient,
            ManuallyDrivenTaskScheduler? foregroundScheduler = null)
        {
            _chatClient = chatClient;
            _foregroundScheduler = foregroundScheduler;
        }

        public Dictionary<AgentSessionId, RunningAgentChatLease> Leases { get; } = new();
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public async Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            if (Leases.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }

            var createTask = AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = definition ?? EchoAgentDefinition,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                ClientOverride = _chatClient,
                DisplayNameOverride = displayNameOverride ?? "sub-agent",
                DescriptionOverride = descriptionOverride,
                ForegroundScheduler = _foregroundScheduler,
            });
            _foregroundScheduler?.RunPending();
            var chat = await createTask;

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            Leases[sessionId] = lease;
            return lease;
        }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => GetOrCreateAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
            => GetOrCreateAsync(sessionId, definition, services, displayNameOverride, descriptionOverride, ct: ct);
    }

    private sealed class ControlledDispatchScenario : IAsyncDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        private readonly CancellationTokenSource dispatchCancellation = new();
        private readonly ManuallyDrivenTaskScheduler? foregroundScheduler;
        private bool enumeratorDisposed;

        public ControlledDispatchScenario(string dispatcherId, bool pauseProcessing = false)
        {
            var testChatClient = new DeterministicTestChatClient();
            foregroundScheduler = pauseProcessing ? new ManuallyDrivenTaskScheduler() : null;
            Factory = new ControllableEchoFactory(testChatClient, foregroundScheduler);
            Client = new SubAgentDispatcherChatClient(
                Factory,
                new DeterministicEmbeddingsProvider(),
                new InMemoryDataAccessLayer(),
                new EntityName("dispatchers", dispatcherId),
                CreateOptions());
            Stream = testChatClient.EnqueueStreamingResponse(isReady: true);
            TestChatClient = testChatClient;

            var messages = new List<ChatMessage> { new(ChatRole.User, "new: controlled task") };
            Enumerator = Client.GetStreamingResponseAsync(
                    messages,
                    cancellationToken: dispatchCancellation.Token)
                .GetAsyncEnumerator();
        }

        public SubAgentDispatcherChatClient Client { get; }

        public ControllableEchoFactory Factory { get; }

        public DeterministicTestChatClient TestChatClient { get; }

        public DeterministicTestChatClient.QueuedStreamResponse Stream { get; }

        public IAsyncEnumerator<ChatResponseUpdate> Enumerator { get; }

        public bool HasPausedProcessingWork => foregroundScheduler?.QueuedCount > 0;

        public void Cancel() => dispatchCancellation.Cancel();

        public void ReleasePausedProcessing()
        {
            Assert.NotNull(foregroundScheduler);
            foregroundScheduler.DrainAndRunInline();
        }

        public async Task<ChatResponseUpdate> ReadRequiredAsync()
        {
            Assert.True(await Enumerator.MoveNextAsync());
            return Enumerator.Current;
        }

        public async Task ReadThroughCreatedAsync()
        {
            var sending = await ReadRequiredAsync();
            Assert.Contains("Sending", sending.Text);
            var created = await ReadRequiredAsync();
            Assert.Contains("Created sub-agent", created.Text);
        }

        public async Task<List<ChatResponseUpdate>> DrainAsync()
        {
            var updates = new List<ChatResponseUpdate>();
            while (await Enumerator.MoveNextAsync())
            {
                updates.Add(Enumerator.Current);
            }

            return updates;
        }

        public async Task WaitForRunningAsync()
        {
            await TestChatClient.WaitForRequestAsync(timeout.Token);
            await WaitForRunningCountAsync(1);
        }

        public Task WaitForIdleAsync() => WaitForRunningCountAsync(0);

        public async Task DisposeEnumeratorAsync()
        {
            if (enumeratorDisposed)
            {
                return;
            }

            enumeratorDisposed = true;
            await Enumerator.DisposeAsync();
        }

        private async Task WaitForRunningCountAsync(int expectedCount)
        {
            var runningItems = Factory.Leases.Values.Single().AgentChat.RunningItems;
            var observableRunningItems = (INotifyCollectionChanged)runningItems;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (runningItems.Count == expectedCount)
                {
                    signal.TrySetResult();
                }
            }

            observableRunningItems.CollectionChanged += OnCollectionChanged;
            try
            {
                if (runningItems.Count == expectedCount)
                {
                    return;
                }

                await signal.Task.WaitAsync(timeout.Token);
            }
            finally
            {
                observableRunningItems.CollectionChanged -= OnCollectionChanged;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeEnumeratorAsync();

            Client.Dispose();
            foregroundScheduler?.DrainAndRunInline();
            foreach (var lease in Factory.Leases.Values)
            {
                await lease.LocalAgentChat.DisposeAsync();
            }
            dispatchCancellation.Dispose();
            timeout.Dispose();
        }
    }

    private sealed class ManuallyDrivenTaskScheduler : TaskScheduler
    {
        private readonly object sync = new();
        private List<Task> queuedTasks = [];
        private bool runInline;

        public int QueuedCount
        {
            get
            {
                lock (sync)
                {
                    return queuedTasks.Count;
                }
            }
        }

        public void RunPending()
        {
            List<Task> pending;
            lock (sync)
            {
                pending = queuedTasks;
                queuedTasks = [];
            }

            foreach (var task in pending)
            {
                TryExecuteTask(task);
            }
        }

        public void DrainAndRunInline()
        {
            List<Task> pending;
            lock (sync)
            {
                runInline = true;
                pending = queuedTasks;
                queuedTasks = [];
            }

            foreach (var task in pending)
            {
                TryExecuteTask(task);
            }
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (sync)
            {
                return queuedTasks.ToArray();
            }
        }

        protected override void QueueTask(Task task)
        {
            lock (sync)
            {
                if (!runInline)
                {
                    queuedTasks.Add(task);
                    return;
                }
            }

            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
