using System.Collections.ObjectModel;
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
        Assert.Empty(lease.AgentChat.RunningItems);
        Assert.True(lease.AgentChat.History.Count > 0);

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
    public async Task Cancellation_InterruptsRunningSubAgent_AndYieldsInterrupted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var testChatClient = new DeterministicTestChatClient();
        var factory = new ControllableEchoFactory(testChatClient);
        var dispatcherName = new EntityName("dispatchers", "interrupt");

        var client = new SubAgentDispatcherChatClient(
            factory,
            new DeterministicEmbeddingsProvider(),
            dataAccessLayer,
            dispatcherName,
            CreateOptions());

        var stream = testChatClient.EnqueueStreamingResponse(isReady: true);

        using var dispatchCts = new CancellationTokenSource();
        var messages = new List<ChatMessage> { new(ChatRole.User, "new: long running task") };
        var enumerator = client.GetStreamingResponseAsync(messages, cancellationToken: dispatchCts.Token)
            .GetAsyncEnumerator(dispatchCts.Token);

        var updates = new List<ChatResponseUpdate>();
        var gotCreated = false;
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                updates.Add(enumerator.Current);
                if (enumerator.Current.Text?.Contains("Created sub-agent") == true)
                {
                    gotCreated = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(gotCreated);

        dispatchCts.Cancel();
        stream.Complete();

        try
        {
            while (await enumerator.MoveNextAsync())
            {
                updates.Add(enumerator.Current);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await enumerator.DisposeAsync();

        Assert.Contains(updates, u => u.Text?.Contains("Interrupted") == true);

        client.Dispose();
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

        public ControllableEchoFactory(DeterministicTestChatClient chatClient)
        {
            _chatClient = chatClient;
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

            var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = definition ?? EchoAgentDefinition,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                ClientOverride = _chatClient,
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
}
