using System.Linq;
using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485 retry: behavioural tests around <see cref="AgentChat"/> (modal accept-once via owner-side
/// dismiss, notes, interrupt idempotence, dispose idempotence, service resolution, and tool-snapshot
/// immutability). These pin the invariants added to <c>AgentChat</c> in this retry. All tests use a
/// synchronous foreground scheduler so <c>PublishModal</c>/<c>PublishModalDismiss</c> and
/// <c>RespondToModalAsync</c> execute inline — no <c>Task.Yield</c> polling loops.
/// </summary>
public sealed class AgentChatRetryTests
{
    private const string EchoAgentJson =
        """
        { "kind": "prompt", "name": "echo-agent", "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": [{ "kind": "web_search", "description": "Search docs" }] }
        """;

    private static AgentDefinition EchoDef => AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);

    private readonly TaskScheduler foregroundScheduler = new SynchronousTaskScheduler();

    private async Task<AgentChatFactory> NewFactoryAsync(AgentSessionId sessionId)
    {
        var store = new InMemoryAgentPersistenceStore();
        var def = EchoDef;
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId.Value!,
                AgentDefinitionJson = BsonDocument.Parse(def.ToJson()),
            }
        });
        var services = new AgentServices { ChatClientOverride = new DeterministicTestChatClient() };
        return new AgentChatFactory(store, services, this.foregroundScheduler);
    }

    [Fact]
    public async Task Information_LocalChat_ReturnsAtomicAgentInformation()
    {
        var sessionId = new AgentSessionId("retry-info-1");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var before = chat.Information;
        var raised = 0;
        AgentInformation observed = default;
        chat.InformationChanged += (_, _) =>
        {
            raised++;
            observed = chat.Information;
        };

        typeof(AgentChat).GetProperty(nameof(AgentChat.DisplayName))!.SetValue(chat, "replacement");
        typeof(AgentChat).GetMethod("PublishInformation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(chat, null);

        Assert.Equal(1, raised);
        Assert.NotEqual(before, observed);
        Assert.Equal("replacement", observed.DisplayName);
        Assert.Equal(observed, chat.Information);
    }

    [Fact]
    public async Task Usage_LocalChat_ReturnsAtomicUsage()
    {
        var sessionId = new AgentSessionId("retry-usage-1");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        await chat.Initialization;
        var raised = 0;
        Usage? observedAtEvent = null;
        chat.UsageChanged += (_, _) =>
        {
            raised++;
            observedAtEvent = chat.Usage;
        };
        // Publish a Usage delta via the same private path the streaming pipeline uses.
        var update = new AgentResponseUpdate
        {
            Contents = new List<AIContent>
            {
                new UsageContent(new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 42, OutputTokenCount = 7 }),
            },
        };
        typeof(AgentChat)
            .GetMethod("AccumulateUsage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(chat, new object[] { update });
        Assert.Equal(1, raised);
        Assert.NotNull(observedAtEvent);
        Assert.Equal(42, observedAtEvent!.Value.TotalInputTokenCount);
        Assert.Equal(7, observedAtEvent.Value.TotalOutputTokenCount);
    }

    [Fact]
    public async Task UsagePublisher_NegativeMetric_RejectsThroughChatPublication()
    {
        var sessionId = new AgentSessionId("retry-usage-invalid");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        await chat.Initialization;
        var before = chat.Usage;
        var raised = 0;
        chat.UsageChanged += (_, _) => raised++;
        var update = new AgentResponseUpdate
        {
            Contents =
            [
                new UsageContent(new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = -1 }),
            ],
        };

        typeof(AgentChat)
            .GetMethod("AccumulateUsage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(chat, [update]);

        Assert.Equal(before, chat.Usage);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task InformationPublisher_InvalidCandidates_RejectThroughChatPublication()
    {
        var sessionId = new AgentSessionId("retry-info-invalid");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var before = chat.Information;
        var invalidCandidates = new[]
        {
            before with { AgentSessionId = "" },
            before with { CurrentModelId = " " },
            before with { AgentDefinition = null! },
        };
        var raised = 0;
        chat.InformationChanged += (_, _) => raised++;
        var publish = typeof(AgentChat).GetMethod(
            "TryPublishInformation",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var candidate in invalidCandidates)
        {
            publish.Invoke(chat, [candidate]);
        }

        Assert.Equal(before, chat.Information);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task InputQueues_LocalChat_ReturnsCommonQueueAggregate()
    {
        var sessionId = new AgentSessionId("retry-queues-1");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        Assert.NotNull(lease.AgentChat.InputQueues);
        Assert.IsAssignableFrom<IAgentInputQueues>(lease.AgentChat.InputQueues);
    }

    [Fact]
    public async Task GetToolSnapshot_MutationAfterRead_DoesNotChangeReturnedSnapshot()
    {
        var sessionId = new AgentSessionId("retry-tools-snap");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        await chat.Initialization;
        var first = chat.GetToolSnapshot();
        var tool = Assert.Single(first);

        await chat.SetToolEnabledAsync(tool.Id, !tool.IsEnabled);

        Assert.Equal(tool.IsEnabled, Assert.Single(first).IsEnabled);
        Assert.Equal(!tool.IsEnabled, Assert.Single(chat.GetToolSnapshot()).IsEnabled);
    }

    [Fact]
    public async Task SetToolEnabledAsync_KnownTool_ChangesStateThenRaisesToolsChanged()
    {
        var sessionId = new AgentSessionId("retry-tool-known");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        await chat.Initialization;
        var tool = Assert.Single(chat.GetToolSnapshot());
        var raised = 0;
        bool? enabledAtEvent = null;
        chat.ToolsChanged += (_, _) =>
        {
            raised++;
            enabledAtEvent = Assert.Single(chat.GetToolSnapshot()).IsEnabled;
        };

        await chat.SetToolEnabledAsync(tool.Id, !tool.IsEnabled);

        Assert.Equal(1, raised);
        Assert.Equal(!tool.IsEnabled, enabledAtEvent);
    }

    [Fact]
    public async Task SetToolEnabledAsync_UnknownTool_ThrowsArgumentException()
    {
        var sessionId = new AgentSessionId("retry-tool-unknown");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        await Assert.ThrowsAsync<ArgumentException>(
            () => lease.AgentChat.SetToolEnabledAsync("no-such-tool", true));
    }

    [Fact]
    public async Task SetToolEnabledAsync_Cancelled_DoesNotMutateOrRaiseEvent()
    {
        var sessionId = new AgentSessionId("retry-tool-cancel");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        await chat.Initialization;
        var beforeSnapshot = chat.GetToolSnapshot();
        var tool = Assert.Single(beforeSnapshot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var raised = 0;
        chat.ToolsChanged += (_, _) => raised++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chat.SetToolEnabledAsync(tool.Id, !tool.IsEnabled, cts.Token));
        var afterSnapshot = chat.GetToolSnapshot();
        Assert.Equal(tool.IsEnabled, Assert.Single(afterSnapshot).IsEnabled);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task RespondToModalAsync_UnknownModal_ThrowsArgumentException()
    {
        var sessionId = new AgentSessionId("retry-modal-unknown");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        await Assert.ThrowsAsync<ArgumentException>(
            () => lease.AgentChat.RespondToModalAsync("no-modal", default));
    }

    [Fact]
    public async Task RespondToModalAsync_CurrentModal_AcceptsExactlyOnce()
    {
        var sessionId = new AgentSessionId("retry-modal-accept");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var modal = new AgentChatModal
        {
            Id = "m-1",
            OwnerAgentId = "owner",
            Title = "t",
            Body = "b",
            Content = new ApprovalModalContent { ApproveLabel = "Y", RejectLabel = "N" },
        };
        chat.PublishModal(modal);
        Assert.Single(chat.Modals);

        var respondTask = chat.RespondToModalAsync("m-1", JsonDocument.Parse("{}").RootElement);
        // On a synchronous scheduler, the RespondToModalAsync foreground continuation has run
        // inline. The modal is still present because dismissal is owner-authoritative.
        Assert.False(respondTask.IsCompleted, "Modal remains present until owner dismisses.");
        Assert.Single(chat.Modals);

        chat.PublishModalDismiss("m-1");
        await respondTask;
        Assert.Empty(chat.Modals);

        await Assert.ThrowsAsync<ArgumentException>(
            () => chat.RespondToModalAsync("m-1", JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public async Task EnqueueSystemNote_ValidText_AppendsSystemNote()
    {
        var sessionId = new AgentSessionId("retry-sys-note");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var before = chat.History.Count;
        chat.EnqueueSystemNote("hello");
        Assert.Equal(before + 1, chat.History.Count);
        Assert.Contains(chat.History[^1].Contents.OfType<TextContent>(), c => c.Text == "hello");
    }

    [Fact]
    public async Task EnqueueHelpNote_ValidText_AppendsHelpNote()
    {
        var sessionId = new AgentSessionId("retry-help-note");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var before = chat.History.Count;
        chat.EnqueueHelpNote("help");
        Assert.Equal(before + 1, chat.History.Count);
        Assert.Contains(chat.History[^1].Contents.OfType<TextContent>(), c => c.Text == "help");
    }

    [Fact]
    public async Task EnqueueTransientDiagnostic_ValidText_AppendsNonPersistedDiagnostic()
    {
        var sessionId = new AgentSessionId("retry-diag-note");
        var store = new InMemoryAgentPersistenceStore();
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId.Value!,
                AgentDefinitionJson = BsonDocument.Parse(EchoDef.ToJson()),
            },
        });
        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            this.foregroundScheduler);
        var lease = await factory.GetAsync(sessionId);
        var chat = (AgentChat)lease.AgentChat;
        chat.EnqueueTransientDiagnostic("diag");
        var last = chat.History[^1];
        Assert.Equal(AgentChatHistoryItem.DiagnosticChatRole, last.Role);
        await lease.DisposeAsync();

        await using var reloadedLease = await factory.GetAsync(sessionId);
        Assert.DoesNotContain(
            reloadedLease.AgentChat.History,
            item => item.Contents.OfType<TextContent>().Any(content => content.Text == "diag"));
    }

    [Fact]
    public async Task Interrupt_NoActiveTurn_IsIdempotent()
    {
        var sessionId = new AgentSessionId("retry-interrupt-idem");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        lease.AgentChat.Interrupt();
        lease.AgentChat.Interrupt();
        lease.AgentChat.Interrupt();
    }

    [Fact]
    public async Task Interrupt_ActiveTurn_CancelsTurnWithoutDisposingChat()
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("blocked")] },
            isReady: false);
        stream.Complete(isReady: false);
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoDef,
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            ForegroundScheduler = this.foregroundScheduler,
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count > 0)
            {
                started.TrySetResult();
            }
        };
        chat.EnqueueUserMessage("start");
        await started.Task.WaitAsync(CancellationToken.None);

        chat.Interrupt();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count == 0)
            {
                stopped.TrySetResult();
            }
        };
        if (chat.RunningItems.Count == 0)
        {
            stopped.TrySetResult();
        }
        await stopped.Task.WaitAsync(CancellationToken.None);

        chat.EnqueueSystemNote("still-usable");
        Assert.Contains(chat.History, item => item.Contents.OfType<TextContent>().Any(content => content.Text == "still-usable"));
    }

    [Fact]
    public async Task InputQueues_ActiveNonCopilotRun_ConsumesAtFutureTurnWithSingleRevisionStep()
    {
        var client = new DeterministicTestChatClient();
        var activeTurn = client.EnqueueStreamingResponse();
        var activeTurnEnd = activeTurn.Complete(isReady: false);
        client.EnqueueStreamingResponse().Complete();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoDef,
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            ForegroundScheduler = this.foregroundScheduler,
        });
        chat.EnqueueUserMessage("first turn");
        await client.WaitForRequestAsync(CancellationToken.None);

        var queues = ((IAgentChat)chat).InputQueues;
        var enqueue = await queues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = queues.DefaultQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "future turn")],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = queues.Snapshot.Revision,
        });
        Assert.Contains(queues.DefaultQueue.Snapshot.Items, item => item.ItemId == enqueue.ItemId);
        var revisionBeforeConsumption = queues.Snapshot.Revision;
        var changedCount = 0;
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queues.Changed += (_, _) =>
        {
            changedCount++;
            if (queues.DefaultQueue.Snapshot.Items.All(item => item.ItemId != enqueue.ItemId))
            {
                consumed.TrySetResult();
            }
        };

        activeTurnEnd.MarkReady();
        await consumed.Task.WaitAsync(CancellationToken.None);

        Assert.Equal(revisionBeforeConsumption + 1, queues.Snapshot.Revision);
        Assert.Equal(1, changedCount);
        Assert.DoesNotContain(queues.DefaultQueue.Snapshot.Items, item => item.ItemId == enqueue.ItemId);
    }

    [Fact]
    public async Task TurnCompleted_TurnPersists_EventRaisedAfterHistoryMutation()
    {
        var store = new InMemoryAgentPersistenceStore();
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("completed")],
            FinishReason = ChatFinishReason.Stop,
        });
        stream.Complete();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoDef,
            ConfiguredStore = store,
            ClientOverride = client,
            ForegroundScheduler = this.foregroundScheduler,
        });
        var completed = new TaskCompletionSource<AgentChatHistoryItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.TurnCompleted += (_, item) =>
        {
            Assert.Contains(item, chat.History);
            completed.TrySetResult(item);
        };

        chat.EnqueueUserMessage("start");
        var item = await completed.Task.WaitAsync(CancellationToken.None);
        var persisted = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = chat.AgentSessionId },
            CancellationToken.None);

        Assert.Contains(item.Contents.OfType<TextContent>(), content => content.Text == "completed");
        Assert.Contains(persisted, message => message.Contents.OfType<TextContent>().Any(content => content.Text == "completed"));
    }

    [Fact]
    public async Task DisposeAsync_RepeatedCall_DisposesOnce()
    {
        var sessionId = new AgentSessionId("retry-dispose-once");
        var factory = await NewFactoryAsync(sessionId);
        var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var disposeStartedField = typeof(AgentChat)
            .GetField("disposeStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await lease.DisposeAsync();
        var afterFirst = (int)disposeStartedField.GetValue(chat)!;
        await lease.DisposeAsync();
        var afterSecond = (int)disposeStartedField.GetValue(chat)!;
        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        await factory.DisposeAsync();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task GetService_KnownService_ReturnsExistingService()
    {
        var sessionId = new AgentSessionId("retry-getservice");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        // The chat resolves itself as an ISubAgentTable service.
        var subAgentTable = ((IServiceProvider)chat).GetService(typeof(ISubAgentTable));
        Assert.NotNull(subAgentTable);
        Assert.Same(chat, subAgentTable);
    }

    // #1226 pattern: inline foreground scheduler so PublishModal / RespondToModalAsync /
    // PublishModalDismiss run synchronously on the calling thread. This eliminates the previous
    // Task.Yield polling loops and makes ordering assertions deterministic (issue #1485 retry).
    private sealed class SynchronousTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => Enumerable.Empty<Task>();
        protected override void QueueTask(Task task) => this.TryExecuteTask(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            => this.TryExecuteTask(task);
    }
}
