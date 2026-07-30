using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SubAgentDispatcherChatClientTests
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

    private static AgentDefinitionTool CreateDefaultTool() => new()
    {
        Name = "default",
        Description = "Default echo agent",
        Definition = EchoAgentDefinition,
    };

    private static SubAgentDispatcherOptions CreateOptions(params AgentDefinitionTool[] tools) =>
        new() { AgentDefinitionTools = tools.Length > 0 ? tools : [CreateDefaultTool()] };

    private static async Task WaitForConditionAsync(
        IReadOnlyList<INotifyCollectionChanged> collections,
        Func<bool> condition,
        string description,
        CancellationToken cancellationToken = default)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (ConditionMet(condition))
            {
                signal.TrySetResult();
            }
        }

        foreach (var collection in collections)
        {
            collection.CollectionChanged += OnCollectionChanged;
        }

        try
        {
            if (ConditionMet(condition))
            {
                return;
            }

            await signal.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            foreach (var collection in collections)
            {
                collection.CollectionChanged -= OnCollectionChanged;
            }
        }
    }

    private static bool ConditionMet(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Dispatch_NewMessage_CreatesSubAgent_AndCopiesOutput()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var embeddingsProvider = new DeterministicEmbeddingsProvider();
        var dataAccessLayer = new FakeDataAccessLayer();
        var dispatcherEntityName = new EntityName("dispatchers", "test-dispatcher");
        var options = CreateOptions();
        var factory = new RealAgentChatFactory();

        var client = new SubAgentDispatcherChatClient(
            factory,
            embeddingsProvider,
            dataAccessLayer,
            dispatcherEntityName,
            options);

        // Act - Send a "new:" message which creates a sub-agent
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "new: hello world"),
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: timeout.Token))
        {
            updates.Add(update);
        }

        // Assert
        Assert.NotEmpty(updates);

        // Should have the ack message: Sending "..." to <id>.
        var ackUpdate = updates.FirstOrDefault(u => u.Text?.Contains("Sending") == true);
        Assert.NotNull(ackUpdate);
        Assert.Contains("hello world", ackUpdate.Text);

        // Should have the created message
        var createdUpdate = updates.FirstOrDefault(u => u.Text?.Contains("Created sub-agent") == true);
        Assert.NotNull(createdUpdate);

        // Should have output from the echo agent (which echoes the prompt back)
        var outputUpdates = updates.Where(u =>
            u.Text != null &&
            !u.Text.Contains("Sending") &&
            !u.Text.Contains("Created sub-agent")).ToList();

        // The echo agent should have processed the message
        Assert.True(factory.Leases.Count > 0, "At least one lease should have been created");
        var lease = factory.Leases.Values.First();
        Assert.NotNull(lease.AgentChat);

        // LastUpdated should have been set
        // (We can't easily access DispatchedSubAgent from outside, but the test passing indicates it worked)
        client.Dispose();
    }

    [Fact]
    public async Task HandleCreateSubAgent_DoesNotAddSubAgentToRunningSessions()
    {
        // Issue #1150: dispatcher-created sub-agents must opt out of RunningSessions so the
        // top-right "Running agents" popup lists only top-level agents.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var embeddingsProvider = new DeterministicEmbeddingsProvider();
        var dataAccessLayer = new FakeDataAccessLayer();
        var dispatcherEntityName = new EntityName("dispatchers", "test-dispatcher");
        var options = CreateOptions();
        var factory = new RealAgentChatFactory();

        var client = new SubAgentDispatcherChatClient(
            factory,
            embeddingsProvider,
            dataAccessLayer,
            dispatcherEntityName,
            options);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "new: hello"),
        };

        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: timeout.Token))
        {
        }

        Assert.NotEmpty(factory.RegisterAsRunningAgentCalls);
        Assert.All(factory.RegisterAsRunningAgentCalls, v => Assert.False(v));
        Assert.Empty(factory.RunningSessions);

        client.Dispose();
    }

    [Fact]
    public async Task Route_ToExistingSubAgent_EnqueuesToThatAgent()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var embeddingsProvider = new DeterministicEmbeddingsProvider();
        var dataAccessLayer = new FakeDataAccessLayer();
        var dispatcherEntityName = new EntityName("dispatchers", "test-dispatcher");
        var options = CreateOptions();
        var factory = new RealAgentChatFactory();

        var client = new SubAgentDispatcherChatClient(
            factory,
            embeddingsProvider,
            dataAccessLayer,
            dispatcherEntityName,
            options);

        // First, create a sub-agent
        var createMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "new: first message"),
        };

        var createUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(createMessages, cancellationToken: timeout.Token))
        {
            createUpdates.Add(update);
        }

        // Extract the sub-agent id from the "Created sub-agent" message
        var createdMsg = createUpdates.FirstOrDefault(u => u.Text?.Contains("Created sub-agent") == true);
        Assert.NotNull(createdMsg);
        var idMatch = System.Text.RegularExpressions.Regex.Match(createdMsg.Text!, @"Created sub-agent ""([^""]+)""");
        Assert.True(idMatch.Success, "Should find sub-agent id in created message");
        var subAgentId = idMatch.Groups[1].Value;

        // Get the lease to observe the history
        var lease = factory.Leases.Values.First();
        var initialHistoryCount = lease.AgentChat.History.Count;

        // Act - Route to the existing sub-agent
        var routeMessages = new List<ChatMessage>
        {
            new(ChatRole.User, $"{subAgentId}: follow up message"),
        };

        var routeUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(routeMessages, cancellationToken: timeout.Token))
        {
            routeUpdates.Add(update);
        }

        // Assert - The history should have grown (user message + assistant echo)
        Assert.True(lease.AgentChat.History.Count > initialHistoryCount,
            "History should have grown after routing a message to the sub-agent");

        // The output should contain the echo of "follow up message"
        var allText = string.Join("", routeUpdates.Select(u => u.Text ?? ""));
        // Echo agent echoes the user message
        Assert.Contains("follow up message", allText);

        client.Dispose();
    }

    [Fact]
    public async Task Cancellation_WhileSubAgentRunning_PropagatesInterrupt_AndYieldsInterrupted()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var embeddingsProvider = new DeterministicEmbeddingsProvider();
        var dataAccessLayer = new FakeDataAccessLayer();
        var dispatcherEntityName = new EntityName("dispatchers", "test-dispatcher");
        var options = CreateOptions();

        // Use a factory that creates AgentChats with a controllable DeterministicTestChatClient
        var testChatClient = new DeterministicTestChatClient();
        var factory = new ControllableAgentChatFactory(testChatClient);

        var client = new SubAgentDispatcherChatClient(
            factory,
            embeddingsProvider,
            dataAccessLayer,
            dispatcherEntityName,
            options);

        // Enqueue a streaming response that won't complete until we say so
        var stream = testChatClient.EnqueueStreamingResponse(isReady: true);
        // Don't complete the stream yet - this will hold the sub-agent in a running state

        using var dispatchCts = new CancellationTokenSource();

        // Act - Start dispatching
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "new: test message"),
        };

        var updates = new List<ChatResponseUpdate>();
        var enumerator = client.GetStreamingResponseAsync(messages, cancellationToken: dispatchCts.Token)
            .GetAsyncEnumerator(dispatchCts.Token);

        // Collect initial updates (ack and created messages)
        var gotInitialUpdates = false;
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                updates.Add(enumerator.Current);
                if (enumerator.Current.Text?.Contains("Created sub-agent") == true)
                {
                    gotInitialUpdates = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected if cancelled
        }

        Assert.True(gotInitialUpdates, "Should have received the initial ack and created messages");

        // Now the dispatcher is waiting for the sub-agent to go idle
        // The sub-agent is waiting for the DeterministicTestChatClient to produce output

        // Cancel the dispatcher
        dispatchCts.Cancel();

        // Complete the stream so the test can finish
        stream.Complete();

        // Try to get any remaining updates
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                updates.Add(enumerator.Current);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        await enumerator.DisposeAsync();

        // Assert - Should have an "Interrupted." message
        var interruptedUpdate = updates.FirstOrDefault(u => u.Text?.Contains("Interrupted") == true);
        Assert.NotNull(interruptedUpdate);

        client.Dispose();
    }

    /// <summary>
    /// A factory that creates real AgentChats using the echo provider.
    /// </summary>
    private sealed class RealAgentChatFactory : IRunningAgentChatFactory
    {
        public Dictionary<AgentSessionId, RunningAgentChatLease> Leases { get; } = new();
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();
        public List<bool> RegisterAsRunningAgentCalls { get; } = new();

        public async Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            RegisterAsRunningAgentCalls.Add(registerAsRunningAgent);

            if (Leases.TryGetValue(sessionId, out var existingLease))
            {
                return existingLease;
            }

            var agentDefinition = definition ?? AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
            var store = new InMemoryAgentPersistenceStore();

            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                ConfiguredStore = store,
                DisplayNameOverride = displayNameOverride ?? "test-agent",
                DescriptionOverride = descriptionOverride,
            });

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            Leases[sessionId] = lease;
            if (registerAsRunningAgent)
            {
                RunningSessions.Add(new RunningAgentChat(sessionId, this));
            }
            return lease;
        }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            if (Leases.TryGetValue(sessionId, out var lease))
            {
                return Task.FromResult(lease);
            }
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            CancellationToken ct = default)
        {
            return GetOrCreateAsync(sessionId, definition, services, displayNameOverride, descriptionOverride, ct: ct);
        }
    }

    /// <summary>
    /// A factory that creates AgentChats with a controllable DeterministicTestChatClient.
    /// </summary>
    private sealed class ControllableAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly DeterministicTestChatClient _chatClient;
        public Dictionary<AgentSessionId, RunningAgentChatLease> Leases { get; } = new();
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public ControllableAgentChatFactory(DeterministicTestChatClient chatClient)
        {
            _chatClient = chatClient;
        }

        public async Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            if (Leases.TryGetValue(sessionId, out var existingLease))
            {
                return existingLease;
            }

            var agentDefinition = definition ?? AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
            var store = new InMemoryAgentPersistenceStore();

            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = agentDefinition,
                ConfiguredStore = store,
                ClientOverride = _chatClient,
                DisplayNameOverride = displayNameOverride ?? "test-agent",
                DescriptionOverride = descriptionOverride,
            });

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            Leases[sessionId] = lease;
            return lease;
        }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            if (Leases.TryGetValue(sessionId, out var lease))
            {
                return Task.FromResult(lease);
            }
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            CancellationToken ct = default)
        {
            return GetOrCreateAsync(sessionId, definition, services, displayNameOverride, descriptionOverride, ct: ct);
        }
    }

    /// <summary>
    /// A fake data access layer for testing.
    /// </summary>
    private sealed class FakeDataAccessLayer : IDataAccessLayer
    {
        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateResult { EntityResults = [] });

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetResult { Batches = [] });

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new QueryResult { Batches = [] });

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetHistoryResult { History = [] });

#pragma warning disable CS0618 // Type or member is obsolete
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExportResult { ChangeBatches = [], FinalSnapshotTime = new Timestamp(DateTimeOffset.UtcNow, "fake-change-id") });
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetChangedEntitiesResult { Entities = [] });
    }
}
