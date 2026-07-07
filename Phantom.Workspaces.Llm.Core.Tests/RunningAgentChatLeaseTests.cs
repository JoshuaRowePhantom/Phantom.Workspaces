using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class RunningAgentChatLeaseTests
{
    [Fact]
    public void SessionId_Property_ReturnsConstructorArgument()
    {
        var sessionId = new AgentSessionId("session-1");
        var lease = new RunningAgentChatLease(sessionId, null!, () => ValueTask.CompletedTask);

        Assert.Equal(sessionId, lease.SessionId);
    }

    [Fact]
    public void AgentChat_Property_ReturnsConstructorArgument()
    {
        var sessionId = new AgentSessionId("session-1");
        var lease = new RunningAgentChatLease(sessionId, null!, () => ValueTask.CompletedTask);

        Assert.Null(lease.AgentChat);
    }

    [Fact]
    public async Task DisposeAsync_CallsOnDisposeCallback()
    {
        var called = false;
        var lease = new RunningAgentChatLease(new AgentSessionId("s"), null!, () =>
        {
            called = true;
            return ValueTask.CompletedTask;
        });

        await lease.DisposeAsync();

        Assert.True(called);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_OnlyCallsCallbackOnce()
    {
        var callCount = 0;
        var lease = new RunningAgentChatLease(new AgentSessionId("s"), null!, () =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        });

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task DisposeAsync_CalledFromDifferentThread_IsSafe()
    {
        var callCount = 0;
        var lease = new RunningAgentChatLease(new AgentSessionId("s"), null!, () =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        });

        await Task.Run(async () => await lease.DisposeAsync());

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCalls_CallbackCalledOnce()
    {
        var callCount = 0;
        var lease = new RunningAgentChatLease(new AgentSessionId("s"), null!, () =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        });

        var t1 = Task.Run(async () => await lease.DisposeAsync());
        var t2 = Task.Run(async () => await lease.DisposeAsync());
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, callCount);
    }
}
