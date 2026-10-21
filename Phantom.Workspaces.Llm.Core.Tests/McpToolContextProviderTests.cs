using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class McpToolContextProviderTests
{
    [Fact]
    public async Task McpToolContextProvider_InitFailure_MarksTerminalAndNotRetriedOnNextInvocation()
    {
        var attempts = 0;
        await using var provider = CreateProvider(_ =>
        {
            attempts++;
            throw new McpException("connect failed");
        });
        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        Assert.Empty(await InvokeAsync(provider));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task McpToolContextProvider_SuccessfulInit_CachesSingleClientAcrossInvocations()
    {
        var attempts = 0;
        var sample = AIFunctionFactory.Create(() => "ok", "sample");
        await using var provider = CreateProvider(_ =>
        {
            attempts++;
            return Task.FromResult<AITool[]>([sample]);
        });
        Assert.Single(await InvokeAsync(provider));
        Assert.Single(await InvokeAsync(provider));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task McpToolContextProvider_TerminalFailure_DoesNotRelaunchOAuthRedirect()
    {
        var attempts = 0;
        await using var provider = CreateProvider(_ =>
        {
            attempts++;
            throw new McpException("authorization failed");
        });
        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        Assert.Empty(await InvokeAsync(provider));
        Assert.Empty(await InvokeAsync(provider));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task McpToolContextProvider_ResetInitialization_ClearsFailureAndDrainsPreviousTransport()
    {
        var attempts = 0;
        var transport = new BlockingOwnedTransport();
        await using var provider = CreateProvider(_ =>
        {
            attempts++;
            throw new McpException("connect failed");
        }, transport);
        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        Assert.Empty(await InvokeAsync(provider));
        provider.ResetInitialization();
        var reconnect = InvokeAsync(provider);
        try
        {
            await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(reconnect.IsCompleted);
            Assert.Equal(1, attempts);
        }
        finally { transport.AllowDispose.TrySetResult(); }
        await Assert.ThrowsAsync<McpException>(() => reconnect);
        Assert.Equal(2, attempts);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Empty(await InvokeAsync(provider));
    }

    [Fact]
    public async Task McpToolContextProvider_ReconnectAfterCredentialCommit_DrainsOldTransportAndCreatesFreshConnection()
    {
        var attempts = 0;
        var transport = new BlockingOwnedTransport();
        await using var provider = CreateProvider(_ =>
        {
            attempts++;
            return Task.FromResult<AITool[]>([]);
        }, transport);
        await InvokeAsync(provider);
        var reconnect = provider.ReconnectAfterCredentialCommitAsync(CancellationToken.None);
        try
        {
            await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(reconnect.IsCompleted);
            Assert.Equal(1, attempts);
        }
        finally { transport.AllowDispose.TrySetResult(); }
        await reconnect;
        await InvokeAsync(provider);
        Assert.Equal(2, attempts);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task McpToolContextProvider_FailedReconnect_IsLatchedUntilExplicitAction()
    {
        var attempts = 0;
        await using var provider = CreateProvider(_ =>
        {
            attempts++;
            if (attempts <= 2) { throw new McpException("connect failed"); }
            return Task.FromResult<AITool[]>([]);
        });
        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        await Assert.ThrowsAsync<McpException>(() => provider.ReconnectAfterCredentialCommitAsync(CancellationToken.None));
        Assert.Empty(await InvokeAsync(provider));
        Assert.Equal(2, attempts);
        await provider.ReconnectAfterCredentialCommitAsync(CancellationToken.None);
        Assert.Empty(await InvokeAsync(provider));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task McpToolContextProvider_CancelledReconnectWait_DoesNotInterruptExistingInitialization()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var provider = CreateProvider(async cancellation =>
        {
            attempts++;
            started.TrySetResult();
            await finish.Task.WaitAsync(cancellation);
            return [];
        });
        var initialization = InvokeAsync(provider);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReconnectAfterCredentialCommitAsync(cancellation.Token));
        finish.TrySetResult();
        await initialization;
        await InvokeAsync(provider);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DisposeAsync_InFlightInitialization_CancelsAndDrainsAttempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = CreateProvider(async cancellation =>
        {
            started.TrySetResult();
            try
            {
                await finish.Task.WaitAsync(cancellation);
                return [];
            }
            finally { cancelled.TrySetResult(); }
        });
        var initialization = InvokeAsync(provider);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await provider.DisposeAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
    }

    private static McpToolContextProvider CreateProvider(
        Func<CancellationToken, Task<AITool[]>> initialize,
        IAsyncDisposable? ownedTransport = null) => new(
            new McpTool { ServerName = "MCP server", Connection = new OAuthConnection { Endpoint = "https://resource.test/mcp" } },
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            boundExecutor: null,
            router: null,
            initializeOverride: initialize,
            ownedTransportOverride: ownedTransport);

    private sealed class BlockingOwnedTransport : IAsyncDisposable
    {
        private int disposals;
        public int DisposeCount => Volatile.Read(ref this.disposals);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref this.disposals) != 1) { return; }
            this.DisposeStarted.TrySetResult();
            await this.AllowDispose.Task;
        }
    }

    private static async Task<AITool[]> InvokeAsync(McpToolContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions { UseProvidedChatClientAsIs = true });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }
}
