using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ReverseExecutionEndToEndTests
{
    private sealed class StubHandler : IReverseExecutionHandler
    {
        private readonly string[] chunks;
        private readonly bool fail;

        public StubHandler(bool fail, params string[] chunks)
        {
            this.fail = fail;
            this.chunks = chunks;
        }

        public RemoteAgentRequest? Received { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            this.Received = request;
            foreach (var chunk in this.chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }

            if (this.fail)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }

    private static async Task<(ReverseExecutionRegistry Registry, IReverseConnection Connection, StubHandler Handler, CancellationTokenSource Cts)>
        ConnectAsync(bool fail = false, params string[] chunks)
    {
        var pair = new InMemoryReverseMessageChannelPair();
        var registry = new ReverseExecutionRegistry();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ConnectionsChanged += (_, _) =>
        {
            if (registry.IsConnected("computer-a"))
            {
                connected.TrySetResult();
            }
        };

        var cts = new CancellationTokenSource();
        var acceptor = new ReverseConnectionAcceptor(registry);
        _ = acceptor.AcceptAsync(pair.ServerEnd, cts.Token);

        var handler = new StubHandler(fail, chunks);
        var worker = new ReverseExecutionWorker(pair.ClientEnd, "computer-a", handler);
        _ = worker.RunAsync(cts.Token);

        await connected.Task;
        Assert.True(registry.TryGetConnection("computer-a", out var connection));
        return (registry, connection, handler, cts);
    }

    [Fact]
    public async Task Execute_StreamsResultBackFromConnectingInstance()
    {
        var (_, connection, handler, cts) = await ConnectAsync(fail: false, "Hello, ", "world");
        try
        {
            var request = new RemoteAgentRequest
            {
                AgentDefinitionJson = "{}",
                Messages = [new ChatMessage(ChatRole.User, "hi")],
            };

            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in connection.ExecuteAsync(request, cts.Token))
            {
                updates.Add(update);
            }

            Assert.Equal("hi", handler.Received!.Messages.Single().Text);
            Assert.Equal("Hello, world", string.Concat(updates.Select(update => update.Text)));
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Fact]
    public async Task Execute_ThroughReverseRemoteChatClient_AggregatesResponse()
    {
        var (registry, _, _, cts) = await ConnectAsync(fail: false, "Hello, ", "world");
        try
        {
            var client = new ReverseRemoteChatClient(registry, "computer-a", "{}");
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);
            Assert.Contains("Hello, world", response.Text);
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Fact]
    public async Task Execute_PropagatesHandlerFailure_AsError()
    {
        var (_, connection, _, cts) = await ConnectAsync(fail: true, "partial");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in connection.ExecuteAsync(
                    new RemoteAgentRequest { AgentDefinitionJson = "{}", Messages = [new ChatMessage(ChatRole.User, "hi")] },
                    cts.Token))
                {
                }
            });
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Fact]
    public async Task Disconnect_FaultsInFlightAndDeregisters()
    {
        var (registry, connection, _, cts) = await ConnectAsync(fail: false, "x");
        Assert.True(registry.IsConnected("computer-a"));

        // Cancelling tears down the worker and server, closing the channel.
        cts.Cancel();

        // The connection's completion resolves once its read loop ends.
        await ((ReverseChannelConnection)connection).Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
