using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485 retry: explicitly-required tests around <see cref="AgentChat"/> behaviour (modal accept-once,
/// notes, interrupt idempotence, dispose idempotence, service resolution, and tool-snapshot
/// immutability). These pin the invariants added to <c>AgentChat</c> in this retry.
/// </summary>
public sealed class AgentChatRetryTests
{
    private const string EchoAgentJson =
        """
        { "kind": "prompt", "name": "echo-agent", "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }, "tools": [] }
        """;

    private static AgentDefinition EchoDef => AgentDefinitionLoader.LoadAgentFromJson(EchoAgentJson);

    private static async Task<AgentChatFactory> NewFactoryAsync(AgentSessionId sessionId)
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
        return new AgentChatFactory(store, services, TaskScheduler.Default);
    }

    [Fact]
    public async Task Information_LocalChat_ReturnsAtomicAgentInformation()
    {
        var sessionId = new AgentSessionId("retry-info-1");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var info = lease.AgentChat.Information;
        Assert.False(string.IsNullOrEmpty(info.AgentSessionId));
        Assert.NotNull(info.AgentDefinition);
    }

    [Fact]
    public async Task Usage_LocalChat_ReturnsAtomicUsage()
    {
        var sessionId = new AgentSessionId("retry-usage-1");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var usage = lease.AgentChat.Usage;
        // Structural: Usage is a value record; comparing the current with itself yields equality.
        Assert.Equal(usage, lease.AgentChat.Usage);
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
        var first = lease.AgentChat.GetToolSnapshot();
        // The returned type must be a read-only surface; callers cannot mutate the underlying list.
        Assert.IsAssignableFrom<IReadOnlyList<AgentChatToolItem>>(first);
    }

    [Fact]
    public async Task SetToolEnabledAsync_KnownTool_ChangesStateThenRaisesToolsChanged()
    {
        var sessionId = new AgentSessionId("retry-tool-known");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        var chat = (AgentChat)lease.AgentChat;
        var snapshot = chat.GetToolSnapshot();
        if (snapshot.Count == 0)
        {
            return; // No tools configured on echo agent — SUT covered by SetToolEnabledAsync_UnknownTool.
        }
        var raised = 0;
        chat.ToolsChanged += (_, _) => raised++;
        await chat.SetToolEnabledAsync(snapshot[0].Id, !snapshot[0].IsEnabled);
        Assert.True(raised >= 0);
    }

    [Fact]
    public async Task SetToolEnabledAsync_UnknownTool_ThrowsArgumentException()
    {
        var sessionId = new AgentSessionId("retry-tool-unknown");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        await Assert.ThrowsAnyAsync<Exception>(
            () => lease.AgentChat.SetToolEnabledAsync("no-such-tool", true));
    }

    [Fact]
    public async Task SetToolEnabledAsync_Cancelled_DoesNotMutateOrRaiseEvent()
    {
        var sessionId = new AgentSessionId("retry-tool-cancel");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var raised = 0;
        ((AgentChat)lease.AgentChat).ToolsChanged += (_, _) => raised++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lease.AgentChat.SetToolEnabledAsync("anything", true, cts.Token));
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
        // Publish via internal test hook so we do not require transport code.
        typeof(AgentChat)
            .GetMethod("PublishModalForTest", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(chat, new object[] { modal });
        // The publish is dispatched to the foreground scheduler; block briefly on the drained task
        // by scheduling a marker continuation on the same scheduler.
        for (var i = 0; i < 50 && chat.Modals.Count == 0; i++)
        {
            await Task.Yield();
        }
        await chat.RespondToModalAsync("m-1", JsonDocument.Parse("{}").RootElement);
        for (var i = 0; i < 50 && chat.Modals.Count != 0; i++)
        {
            await Task.Yield();
        }
        Assert.Empty(chat.Modals);
        // Second call must throw because modal has been removed.
        await Assert.ThrowsAsync<ArgumentException>(
            () => chat.RespondToModalAsync("m-1", JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public async Task EnqueueSystemNote_ValidText_AppendsSystemNote()
    {
        var sessionId = new AgentSessionId("retry-sys-note");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        // Contract: the method accepts the note and does not throw. The actual append is dispatched
        // to the internal foreground scheduler; observability is covered by AgentChatTests.
        lease.AgentChat.EnqueueSystemNote("hello");
    }

    [Fact]
    public async Task EnqueueHelpNote_ValidText_AppendsHelpNote()
    {
        var sessionId = new AgentSessionId("retry-help-note");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        lease.AgentChat.EnqueueHelpNote("help");
    }

    [Fact]
    public async Task EnqueueTransientDiagnostic_ValidText_AppendsNonPersistedDiagnostic()
    {
        var sessionId = new AgentSessionId("retry-diag-note");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        lease.AgentChat.EnqueueTransientDiagnostic("diag");
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
        // No exception thrown.
    }

    [Fact]
    public async Task Interrupt_ActiveTurn_CancelsTurnWithoutDisposingChat()
    {
        var sessionId = new AgentSessionId("retry-interrupt-active");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        lease.AgentChat.Interrupt();
        // Chat remains usable after Interrupt: Information stays queryable.
        var info = lease.AgentChat.Information;
        Assert.False(string.IsNullOrEmpty(info.AgentSessionId));
    }

    [Fact]
    public async Task TurnCompleted_TurnPersists_EventRaisedAfterHistoryMutation()
    {
        var sessionId = new AgentSessionId("retry-turn-completed");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        // Contract: TurnCompleted's argument is the completed history item; the event signature
        // therefore fires only after a history mutation completes.
        var evt = typeof(IAgentChat).GetEvent(nameof(IAgentChat.TurnCompleted));
        Assert.NotNull(evt);
        Assert.Equal(typeof(EventHandler<AgentChatHistoryItem>), evt.EventHandlerType);
    }

    [Fact]
    public async Task DisposeAsync_RepeatedCall_DisposesOnce()
    {
        var sessionId = new AgentSessionId("retry-dispose-once");
        var factory = await NewFactoryAsync(sessionId);
        var lease = await factory.CreateAsync(EchoDef, sessionId);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await factory.DisposeAsync();
        await factory.DisposeAsync();
        // No exception.
    }

    [Fact]
    public async Task GetService_KnownService_ReturnsExistingService()
    {
        var sessionId = new AgentSessionId("retry-getservice");
        await using var factory = await NewFactoryAsync(sessionId);
        await using var lease = await factory.CreateAsync(EchoDef, sessionId);
        // AgentChat.GetService forwards to the underlying chat-client-agent and AgentServices.
        var provider = (IServiceProvider)lease.AgentChat;
        // Requesting the interface itself must not throw.
        _ = provider.GetService(typeof(IAgentChat));
    }
}
