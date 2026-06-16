using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ReverseExecutionClientHostTests
{
    [Fact]
    public async Task RunAsync_ConnectsRegistersAndReportsDisconnect()
    {
        var pair = new InMemoryReverseMessageChannelPair();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<bool>();
        var factoryCalls = 0;
        using var cancellation = new CancellationTokenSource();

        Task<IReverseMessageChannel> CreateChannel(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(pair.ClientEnd);
        }

        // The backoff cancels the loop so the test ends deterministically after one disconnect.
        Task Backoff(int attempt, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        var host = new ReverseExecutionClientHost("client-a", new EmptyHandler(), CreateChannel, Backoff);
        host.ConnectionStateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }

            if (state)
            {
                connected.TrySetResult();
            }
            else
            {
                disconnected.TrySetResult();
            }
        };

        var runTask = host.RunAsync(cancellation.Token);

        await connected.Task;
        Assert.True(host.IsConnected);

        var serverEnd = pair.ServerEnd;
        var register = await serverEnd.ReceiveAsync(CancellationToken.None);
        Assert.NotNull(register);
        Assert.Equal(ReverseFrame.Types.Register, register!.Type);
        Assert.Equal("client-a", register.ClientInstanceId);

        // Closing the server end ends the worker, which drives the host to report a disconnect.
        await serverEnd.DisposeAsync();
        await disconnected.Task;
        await runTask;

        Assert.False(host.IsConnected);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(new[] { true, false }, states);
    }

    private sealed class EmptyHandler : IReverseExecutionHandler
    {
        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
