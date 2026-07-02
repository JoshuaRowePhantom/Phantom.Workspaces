using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class RunningAgentChatTests
{
    [Fact]
    public void RunningAgentChat_SessionId_MatchesConstructorArg()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();

        var entry = new RunningAgentChat(sessionId, factory);

        Assert.Equal(sessionId, entry.SessionId);
    }

    [Fact]
    public async Task RunningAgentChat_AcquireLeaseAsync_DelegatesToFactory()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();
        var entry = new RunningAgentChat(sessionId, factory);

        var lease = await entry.AcquireLeaseAsync();

        Assert.Equal(1, factory.GetAsyncCallCount);
        Assert.Equal(sessionId, factory.LastRequestedSessionId);
        Assert.NotNull(lease);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RunningAgentChat_AcquireLeaseAsync_PassesCancellationTokenToFactory()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();
        var entry = new RunningAgentChat(sessionId, factory);
        using var cts = new CancellationTokenSource();

        await entry.AcquireLeaseAsync(cts.Token);

        Assert.Equal(cts.Token, factory.LastCancellationToken);
    }

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        public int GetAsyncCallCount { get; private set; }
        public AgentSessionId LastRequestedSessionId { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            GetAsyncCallCount++;
            LastRequestedSessionId = sessionId;
            LastCancellationToken = ct;
            var lease = new RunningAgentChatLease(sessionId, null!, () => ValueTask.CompletedTask);
            return Task.FromResult(lease);
        }
    }
}
