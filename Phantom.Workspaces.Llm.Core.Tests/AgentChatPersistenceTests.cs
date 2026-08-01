using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatPersistenceTests
{
    private static readonly string ParentAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "parent-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static readonly string SubAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "sub-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static AgentDefinition ParentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(ParentAgentDefinitionJson);

    private static AgentDefinition SubDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(SubAgentDefinitionJson);

    private static async Task<AgentChat> CreateParentChatAsync(
        InMemoryAgentPersistenceStore store,
        string? agentSessionId = null,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null)
    {
        var createTask = AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = ParentDefinition,
            AgentSessionId = agentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent",
            AgentServices = services,
            ForegroundScheduler = foregroundScheduler,
        });

        // Initialization now unconditionally dispatches session init onto the foreground scheduler
        // and awaits it (issue #1100). A CapturingTaskScheduler only runs work when driven, so run
        // the queued init task here to let creation complete; the restore-time sub-agent stub adds
        // it queues stay pending for the test to drain and observe.
        if (foregroundScheduler is CapturingTaskScheduler capturing)
        {
            capturing.RunPending();
        }

        return await createTask;
    }

    private static AgentChatFactory CreateFactory(InMemoryAgentPersistenceStore store) =>
        new(store, new AgentServices { ChatClientOverride = new DeterministicTestChatClient() }, TaskScheduler.Default);

    /// <summary>
    /// Queues tasks without executing them until <see cref="Drain"/> is called.
    /// </summary>
    private sealed class CapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> _queue = [];
        // After Drain() is called, tasks are executed inline immediately when queued.
        // This prevents a deadlock during AgentChat.DisposeAsync: when the CTS is
        // cancelled, RunProcessLoopAsync's continuation is queued here; without
        // auto-drain that continuation would never run and processTask would hang.
        private volatile bool _autoDrain;

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                var tasks = _queue.ToList();
                _queue.Clear();
                foreach (var task in tasks)
                    TryExecuteTask(task);
            }
            _autoDrain = true;
        }

        /// <summary>
        /// Executes the tasks currently queued in a single pass, leaving any tasks they queue as a
        /// side effect pending (and without enabling auto-drain). Used to drive AgentChat
        /// initialization to completion without also running the init-queued mutations.
        /// </summary>
        public void RunPending()
        {
            var tasks = _queue.ToList();
            _queue.Clear();
            foreach (var task in tasks)
                TryExecuteTask(task);
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _queue;
        protected override void QueueTask(Task task)
        {
            if (_autoDrain)
                TryExecuteTask(task);
            else
                _queue.Add(task);
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    [Fact]
    public async Task GetOrCreateAsync_AddsSubAgentLink()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var parent = await CreateParentChatAsync(store);

        await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");

        var childIds = await store.ReadSubAgentChildIdsAsync(parent.AgentSessionId);
        Assert.Single(childIds);
    }

    [Fact]
    public async Task InitializeAsync_RestoresSubAgents_FromManifest()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        Assert.Single(restoredParent.SubAgents);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_HasCorrectAgentDefinition()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(restoredParent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();
        Assert.Equal("sub-agent", lease.AgentChat.AgentDefinition?.Name);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_ChatHistoryLoaded()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;
        string childSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            childSessionId = ((AgentChat)Assert.Single(parent.SubAgents)).AgentSessionId;

            // Write a message into the child's session in the store
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent { AgentSessionId = childSessionId },
                NewMessages = [new ChatMessage(ChatRole.User, "hello from history")],
            });

            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(restoredParent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();
        Assert.True(lease.AgentChat.History.Count > 0);
    }

    [Fact]
    public async Task InitializeMcpTools_OnSuccess_ToolListingDiagnosticIsNotPersisted()
    {
        // The tool-listing diagnostic emitted when a toolset finishes loading enters the live
        // History via AddHistoryItem and is never routed through the persistence store, so it is
        // present in the transcript but absent after a reload (issue #1072). This exercises the
        // shared diagnostic path used by both MCP servers and custom toolsets.
        var store = new InMemoryAgentPersistenceStore();
        var toolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
            kind: "scripted_kind",
            createToolsetAsync: (_, _) => Task.FromResult<AIContextProvider?>(
                ToolsetFactory.CreateFixedToolset(new WebSearchTool())));

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "echo-agent",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                  "tools": [ { "kind": "scripted_kind", "description": "Scripted toolset" } ]
                }
                """),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "persistence-toolset-chat",
            AgentServices = new AgentServices { ToolsetFactory = toolsetFactory },
        });

        static string DiagnosticText(AgentChatHistoryItem item)
            => string.Concat(item.Contents.OfType<TextContent>().Select(static content => content.Text));

        Assert.Contains(
            chat.History,
            item => DiagnosticText(item).Contains("Opened toolset 'scripted_kind'. Loaded tools", StringComparison.Ordinal));

        var persisted = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId },
            CancellationToken.None);
        Assert.DoesNotContain(
            persisted,
            message => message.Text is not null
                && message.Text.Contains("Opened toolset", StringComparison.Ordinal));
    }

    // ─── Fix #1187: hosted Copilot sub-agents persist a full, well-formed
    //     AgentDefinition and rehydrate a full definition on restore, even for legacy
    //     rows whose AgentDefinitionJson was never written. ────────────────────────────

    [Fact]
    public async Task AgentChat_CreateSubAgent_PersistsFullAgentDefinitionJson()
    {
        // Fix #1187: after GetOrCreateAsync writes the sub-agent's AgentDefinition to the
        // store, restoring the child session must return a fully-populated PromptAgent
        // (kind/name/model.provider) — not the empty two-field synthetic the router used to
        // produce.
        var store = new InMemoryAgentPersistenceStore();
        string childSessionId;
        await using (var parent = await CreateParentChatAsync(store))
        {
            _ = await parent.GetOrCreateAsync("agent-1187p", SubDefinition, "tool-call-1187p");
            childSessionId = ((AgentChat)Assert.Single(parent.SubAgents)).AgentSessionId;
        }

        var restored = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = childSessionId },
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Value.AgentDefinitionJson);
        var definition = AgentDefinition.FromJson(restored.Value.AgentDefinitionJson!.ToJson());
        var prompt = Assert.IsType<PromptAgent>(definition);
        Assert.NotNull(prompt.Model);
        Assert.False(string.IsNullOrEmpty(prompt.Name));
    }

    [Fact]
    public void AgentDefinition_HostedCopilotSubAgent_RoundTripsThroughJson()
    {
        // Fix #1187: serializing then deserializing the canonical hosted Copilot sub-agent
        // AgentDefinition preserves provider, model.id, name, and displayName.
        var original = CopilotSubAgentDefinitionDefaults.Create(
            subAgentSessionId: "session-1187-roundtrip",
            displayName: "Roundtrip Display",
            description: "Roundtrip description",
            name: "roundtrip-name");

        var roundTripped = AgentDefinition.FromJson(original.ToJson());

        Assert.NotNull(roundTripped);
        var originalPrompt = Assert.IsType<PromptAgent>(original);
        var roundTrippedPrompt = Assert.IsType<PromptAgent>(roundTripped);
        Assert.Equal(originalPrompt.Model?.Provider, roundTrippedPrompt.Model?.Provider);
        Assert.Equal(originalPrompt.Model?.Id, roundTrippedPrompt.Model?.Id);
        Assert.Equal(originalPrompt.Name, roundTrippedPrompt.Name);
        Assert.Equal(originalPrompt.DisplayName, roundTrippedPrompt.DisplayName);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_HasFullAgentDefinition()
    {
        // Fix #1187 (extends InitializeAsync_RestoredSubAgent_HasCorrectAgentDefinition):
        // for a hosted Copilot sub-agent whose AgentDefinitionJson was written via the
        // full-definition path, restore yields an AgentDefinition whose Model.Provider is
        // github-copilot-subagent.
        var store = new InMemoryAgentPersistenceStore();
        var hostedDefinition = CopilotSubAgentDefinitionDefaults.Create(
            subAgentSessionId: "child-1187-hosted",
            displayName: null,
            description: null,
            name: null);
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-hosted-1187", hostedDefinition, "tool-call-hosted-1187");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(restoredParent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();
        var promptAgent = Assert.IsType<PromptAgent>(lease.AgentChat.AgentDefinition);
        Assert.Equal("github-copilot-subagent", promptAgent.Model?.Provider);
    }

    [Fact]
    public async Task InitializeAsync_LegacySubAgentWithMissingDefinitionJson_RehydratesDefaultFullDefinition()
    {
        // Fix #1187: legacy hosted sub-agent rows persisted before the full-definition path
        // existed have AgentDefinitionJson = null. InitializeAsync must substitute the
        // canonical full hosted-Copilot sub-agent definition rather than throwing "Agent
        // definition could not be resolved" (the underlying cause behind #1186).
        var store = new InMemoryAgentPersistenceStore();
        var legacyChildSessionId = "legacy-child-1187";

        // Simulate legacy: session exists, but no AgentDefinitionJson was ever written.
        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = legacyChildSessionId,
                    AgentDefinitionJson = null,
                },
            },
            CancellationToken.None);

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = null,
            AgentSessionId = legacyChildSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "legacy-restore",
        });

        var prompt = Assert.IsType<PromptAgent>(chat.AgentDefinition);
        Assert.Equal("github-copilot-subagent", prompt.Model?.Provider);
        Assert.False(string.IsNullOrEmpty(prompt.Model?.Id));
    }
}
