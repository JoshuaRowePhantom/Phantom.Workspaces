using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SubAgentTests
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

    private static AgentChat CreateMinimalChat() =>
        AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            ForegroundScheduler = TaskScheduler.Default,
        }).GetAwaiter().GetResult();

    [Fact]
    public void SubAgent_LazyPath_AgentChat_IsNullBeforeAcquire()
    {
        var sessionId = new AgentSessionId("lazy-session");
        var factory = new FakeRunningAgentChatFactory();

        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)factory);

        Assert.Null(subAgent.AgentChat);
    }

    [Fact]
    public async Task SubAgent_LazyPath_AcquireLeaseAsync_DelegatesToFactory()
    {
        var sessionId = new AgentSessionId("lazy-session");
        var factory = new FakeRunningAgentChatFactory();
        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)factory);

        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.Equal(1, factory.GetAsyncCallCount);
        Assert.Equal(sessionId, factory.LastRequestedSessionId);
    }

    [Fact]
    public async Task SubAgent_LazyPath_AcquireLeaseAsync_ReturnsLease()
    {
        var sessionId = new AgentSessionId("lazy-session");
        var factory = new FakeRunningAgentChatFactory();
        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)factory);

        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.NotNull(lease);
    }

    [Fact]
    public async Task SubAgent_LazyPath_NoFactory_AcquireLeaseAsync_Throws()
    {
        var sessionId = new AgentSessionId("lazy-session");
        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => subAgent.AcquireLeaseAsync());
    }

    [Fact]
    public async Task SubAgent_EagerPath_AgentChat_IsNotNull()
    {
        var sessionId = new AgentSessionId("eager-session");
        await using var chat = CreateMinimalChat();
        var factory = new FakeRunningAgentChatFactory();

        var subAgent = new SubAgent(sessionId, chat, factory);

        Assert.NotNull(subAgent.AgentChat);
        Assert.Same(chat, subAgent.AgentChat);
    }

    [Fact]
    public async Task SubAgent_EagerPath_AcquireLeaseAsync_DelegatesToFactory()
    {
        var sessionId = new AgentSessionId("eager-session");
        await using var chat = CreateMinimalChat();
        var factory = new FakeRunningAgentChatFactory();
        var subAgent = new SubAgent(sessionId, chat, factory);

        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.Equal(1, factory.GetAsyncCallCount);
    }

    [Fact]
    public void SubAgent_LazyPath_IRunningSubAgent_AgentId_ReturnsSessionIdValue()
    {
        var sessionId = new AgentSessionId("lazy-id");
        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)null);

        Assert.Equal("lazy-id", ((IRunningSubAgent)subAgent).AgentId);
    }

    [Fact]
    public void SubAgent_LazyPath_IRunningSubAgent_CompletionState_ReturnsUnknown()
    {
        var sessionId = new AgentSessionId("lazy-state");
        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)null);

        Assert.Equal(AgentChatCompletionState.Unknown, ((IRunningSubAgent)subAgent).CompletionState);
    }

    [Fact]
    public void SubAgent_LazyPath_IRunningSubAgent_SubAgents_ReturnsEmpty()
    {
        var sessionId = new AgentSessionId("lazy-subs");
        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)null);

        Assert.Empty(((IRunningSubAgent)subAgent).SubAgents);
    }

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        public int GetAsyncCallCount { get; private set; }
        public AgentSessionId LastRequestedSessionId { get; private set; }

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            GetAsyncCallCount++;
            LastRequestedSessionId = sessionId;
            var lease = new RunningAgentChatLease(sessionId, null!, () => ValueTask.CompletedTask);
            return Task.FromResult(lease);
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
            => GetAsync(sessionId, ct);
    }
}
