using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.ObjectModel;
using System.Reflection;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SubAgentLeaseLifetimeTests
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
    public void SubAgent_AgentChat_IsNotPublic()
    {
        var subAgentType = typeof(SubAgent);
        var agentChatProperty = subAgentType.GetProperty("AgentChat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(agentChatProperty);
        
        var getMethod = agentChatProperty.GetMethod;
        Assert.NotNull(getMethod);
        Assert.False(getMethod.IsPublic, "SubAgent.AgentChat getter should not be public");
    }

    [Fact]
    public async Task SubAgent_AcquireLeaseAsync_ReturnsLiveAgentChat()
    {
        var sessionId = new AgentSessionId("eager-session");
        await using var chat = CreateMinimalChat();
        var factory = new FakeRunningAgentChatFactory();
        factory.RegisterChat(sessionId, chat);

        var subAgent = new SubAgent(sessionId, chat, factory);
        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.NotNull(lease);
        Assert.NotNull(lease.AgentChat);
        Assert.Same(chat, lease.AgentChat);
    }

    [Fact]
    public async Task SubAgent_AcquireLeaseAsync_MaterialisesAgentChat_ForLazyStub()
    {
        var sessionId = new AgentSessionId("lazy-session");
        await using var chat = CreateMinimalChat();
        var factory = new FakeRunningAgentChatFactory();
        factory.RegisterChat(sessionId, chat);

        var subAgent = new SubAgent(sessionId, (IRunningAgentChatFactory?)factory);

        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.NotNull(lease);
        Assert.NotNull(lease.AgentChat);
        Assert.Same(chat, lease.AgentChat);
    }

    [Fact]
    public async Task SubAgent_AfterLeaseDisposed_CanAcquireNewLease()
    {
        var sessionId = new AgentSessionId("reacquire-session");
        await using var chat = CreateMinimalChat();
        var factory = new FakeRunningAgentChatFactory();
        factory.RegisterChat(sessionId, chat);

        var subAgent = new SubAgent(sessionId, chat, factory);

        await using (var lease1 = await subAgent.AcquireLeaseAsync())
        {
            Assert.NotNull(lease1.AgentChat);
        }

        await using var lease2 = await subAgent.AcquireLeaseAsync();
        Assert.NotNull(lease2.AgentChat);
    }

    [Fact]
    public async Task AgentSessionToolset_InvokeSubAgentTool_DoesNotRegisterChildInRunningSessions()
    {
        // Issue #1205: AgentSessionToolset acquires sub-agent leases via SubAgent.AcquireLeaseAsync
        // (both the tool-registration path at line 106 and the invoke path at line 928). Those
        // acquisitions must NOT register the child in RunningSessions — the SubAgent code path
        // opts out via registerAsRunningAgent:false so children never leak into the flyout.
        var sessionId = new AgentSessionId("toolset-invoke-1205");
        await using var chat = CreateMinimalChat();
        var factory = new FakeRunningAgentChatFactory();
        factory.RegisterChat(sessionId, chat);

        var subAgent = new SubAgent(sessionId, chat, factory);
        await using var lease = await subAgent.AcquireLeaseAsync();

        Assert.False(factory.LastRegisterAsRunningAgent);
        Assert.Empty(factory.RunningSessions);
    }

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly Dictionary<AgentSessionId, AgentChat> _registeredChats = new();

        public void RegisterChat(AgentSessionId sessionId, AgentChat chat)
        {
            _registeredChats[sessionId] = chat;
        }

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            LastRegisterAsRunningAgent = registerAsRunningAgent;
            if (registerAsRunningAgent)
            {
                RunningSessions.Add(new RunningAgentChat(sessionId, this));
            }
            if (_registeredChats.TryGetValue(sessionId, out var chat))
            {
                var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
                return Task.FromResult(lease);
            }
            throw new InvalidOperationException($"No chat registered for session {sessionId}");
        }

        public bool LastRegisterAsRunningAgent { get; private set; } = true;

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
            => GetAsync(sessionId, ct: ct);
    }
}
