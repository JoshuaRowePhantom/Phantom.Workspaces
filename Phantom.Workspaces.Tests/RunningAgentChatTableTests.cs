using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentChatTableTests
{
    private static Task<AgentChat> CreateEchoAgentChatAsync()
        => AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinition.FromJson("""
                {
                    "kind": "prompt",
                    "name": "test-echo",
                    "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                    "tools": []
                }
                """),
            AgentSessionId = Guid.NewGuid().ToString("n"),
        });

    [Fact]
    public async Task AcquireAsync_SameSessionKey_ReturnsSameAgentChat()
    {
        var table = new RunningAgentChatTable();
        var key = "test-session-same-chat";

        var lease1 = await table.AcquireAsync(key, CreateEchoAgentChatAsync);
        var lease2 = await table.AcquireAsync(key, CreateEchoAgentChatAsync);

        try
        {
            Assert.Same(lease1.AgentChat, lease2.AgentChat);
        }
        finally
        {
            await lease1.DisposeAsync();
            await lease2.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_SameSessionKey_FactoryCalledOnce()
    {
        var table = new RunningAgentChatTable();
        var key = "test-session-factory-once";
        var callCount = 0;

        Task<AgentChat> Factory()
        {
            callCount++;
            return CreateEchoAgentChatAsync();
        }

        var lease1 = await table.AcquireAsync(key, Factory);
        var lease2 = await table.AcquireAsync(key, Factory);

        try
        {
            Assert.Equal(1, callCount);
        }
        finally
        {
            await lease1.DisposeAsync();
            await lease2.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_DifferentSessionKeys_ReturnDifferentChats()
    {
        var table = new RunningAgentChatTable();

        var lease1 = await table.AcquireAsync("key-a", CreateEchoAgentChatAsync);
        var lease2 = await table.AcquireAsync("key-b", CreateEchoAgentChatAsync);

        try
        {
            Assert.NotSame(lease1.AgentChat, lease2.AgentChat);
        }
        finally
        {
            await lease1.DisposeAsync();
            await lease2.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseLastLease_DisposesChat()
    {
        var table = new RunningAgentChatTable();
        var key = "test-session-release-last";
        var callCount = 0;

        Task<AgentChat> Factory()
        {
            callCount++;
            return CreateEchoAgentChatAsync();
        }

        var lease1 = await table.AcquireAsync(key, Factory);
        var lease2 = await table.AcquireAsync(key, Factory);
        Assert.Equal(1, callCount);

        await lease1.DisposeAsync();
        await lease2.DisposeAsync();

        // After all leases released, a new acquire should call factory again
        var lease3 = await table.AcquireAsync(key, Factory);
        try
        {
            Assert.Equal(2, callCount);
        }
        finally
        {
            await lease3.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseFirstLease_ChatStillAlive()
    {
        var table = new RunningAgentChatTable();
        var key = "test-session-release-first";
        var callCount = 0;

        Task<AgentChat> Factory()
        {
            callCount++;
            return CreateEchoAgentChatAsync();
        }

        var lease1 = await table.AcquireAsync(key, Factory);
        var lease2 = await table.AcquireAsync(key, Factory);

        await lease1.DisposeAsync();

        // After one lease released, new acquire on same key should NOT call factory again
        var lease3 = await table.AcquireAsync(key, Factory);
        try
        {
            Assert.Equal(1, callCount);
            Assert.Same(lease2.AgentChat, lease3.AgentChat);
        }
        finally
        {
            await lease2.DisposeAsync();
            await lease3.DisposeAsync();
        }
    }
}
