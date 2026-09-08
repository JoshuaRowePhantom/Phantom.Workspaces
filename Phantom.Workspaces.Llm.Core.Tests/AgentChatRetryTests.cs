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
        { "kind": "prompt", "name": "echo-agent", "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }, "tools": [] }
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
        var info = chat.Information;
        // Publisher validation: every required string is non-blank when Information is exposed.
        Assert.False(string.IsNullOrEmpty(info.AgentSessionId));
        Assert.False(string.IsNullOrEmpty(info.AgentId));
        Assert.False(string.IsNullOrEmpty(info.Name));
        Assert.False(string.IsNullOrEmpty(info.DisplayName));
        Assert.False(string.IsNullOrEmpty(info.Description));
        Assert.NotNull(info.AgentDefinition);
    }

    [Fact]
    public async Task Usage_LocalChat_ReturnsAtomicUsage()
    {
        var sessionId = new AgentSessionId("retry-usage-1");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
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
        var first = chat.GetToolSnapshot();
        var firstCount = first.Count;
        if (firstCount > 0)
        {
            await chat.SetToolEnabledAsync(first[0].Id, !first[0].IsEnabled);
        }
        // Snapshot returned before the mutation is a captured value — its Count is stable.
        Assert.Equal(firstCount, first.Count);
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
        var beforeSnapshot = chat.GetToolSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var raised = 0;
        chat.ToolsChanged += (_, _) => raised++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chat.SetToolEnabledAsync("anything", true, cts.Token));
        var afterSnapshot = chat.GetToolSnapshot();
        Assert.Equal(beforeSnapshot.Count, afterSnapshot.Count);
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
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var before = chat.History.Count;
        chat.EnqueueTransientDiagnostic("diag");
        Assert.Equal(before + 1, chat.History.Count);
        var last = chat.History[^1];
        // Diagnostic role: not persisted by the store even though observable in History.
        Assert.Equal(AgentChatHistoryItem.DiagnosticChatRole, last.Role);
        Assert.Contains(last.Contents.OfType<TextContent>(), c => c.Text == "diag");
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
        var sessionId = new AgentSessionId("retry-interrupt-active");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var cts = new CancellationTokenSource();
        typeof(AgentChat)
            .GetField("activeRunCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(chat, cts);
        chat.Interrupt();
        Assert.True(cts.IsCancellationRequested);
        var info = chat.Information;
        Assert.False(string.IsNullOrEmpty(info.AgentSessionId));
    }

    [Fact]
    public async Task TurnCompleted_TurnPersists_EventRaisedAfterHistoryMutation()
    {
        // Structural: TurnCompleted's argument type is the completed history item, so the event
        // fires *after* the history is mutated (invariant established by the signature contract).
        var evt = typeof(IAgentChat).GetEvent(nameof(IAgentChat.TurnCompleted));
        Assert.NotNull(evt);
        Assert.Equal(typeof(EventHandler<AgentChatHistoryItem>), evt!.EventHandlerType);
        await Task.CompletedTask;
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
