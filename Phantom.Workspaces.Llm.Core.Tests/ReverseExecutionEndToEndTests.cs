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
        private readonly bool failTool;

        public StubHandler(bool fail, bool failTool = false, params string[] chunks)
        {
            this.fail = fail;
            this.failTool = failTool;
            this.chunks = chunks;
        }

        public RemoteAgentRequest? Received { get; private set; }
        public TrustedToolRequest? ReceivedToolRequest { get; private set; }

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

        public Task HandleStreamAsync(
            string streamKind,
            string openPayloadJson,
            Phantom.Workspaces.Llm.Shell.IStreamMessageChannel channel,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken)
        {
            this.ReceivedToolRequest = request;
            if (this.failTool)
                throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
    }

    private static async Task<(ReverseExecutionRegistry Registry, IReverseConnection Connection, StubHandler Handler, CancellationTokenSource Cts)>
        ConnectAsync(bool fail = false, bool failTool = false, params string[] chunks)
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

        var handler = new StubHandler(fail, failTool, chunks);
        var worker = new ReverseExecutionWorker(pair.ClientEnd, "computer-a", handler);
        _ = worker.RunAsync(cts.Token);

        await connected.Task;
        Assert.True(registry.TryGetConnection("computer-a", out var connection));
        return (registry, connection, handler, cts);
    }

    [Fact]
    public async Task Execute_StreamsResultBackFromConnectingInstance()
    {
        var (_, connection, handler, cts) = await ConnectAsync(fail: false, failTool: false, "Hello, ", "world");
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
        var (registry, _, _, cts) = await ConnectAsync(fail: false, failTool: false, "Hello, ", "world");
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
        var (_, connection, _, cts) = await ConnectAsync(fail: true, failTool: false, "partial");
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
        var (registry, connection, _, cts) = await ConnectAsync(fail: false, failTool: false, "x");
        Assert.True(registry.IsConnected("computer-a"));

        // Cancelling tears down the worker and server, closing the channel.
        cts.Cancel();

        // The connection's completion resolves once its read loop ends.
        await ((ReverseChannelConnection)connection).Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunTool_ExecutesOnConnectingInstance()
    {
        var (_, connection, handler, cts) = await ConnectAsync();
        try
        {
            var request = new TrustedToolRequest
            {
                ToolTypeName = "git-workspace-scan",
                ToolEntityId = Guid.NewGuid().ToString(),
                TargetClientInstance = "computer-a",
            };

            await ((ReverseChannelConnection)connection).RunToolAsync(request, cts.Token);

            Assert.NotNull(handler.ReceivedToolRequest);
            Assert.Equal("git-workspace-scan", handler.ReceivedToolRequest!.ToolTypeName);
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Fact]
    public async Task RunTool_PropagatesHandlerFailure()
    {
        var (_, connection, _, cts) = await ConnectAsync(failTool: true);
        try
        {
            var request = new TrustedToolRequest
            {
                ToolTypeName = "git-workspace-scan",
                ToolEntityId = Guid.NewGuid().ToString(),
                TargetClientInstance = "computer-a",
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ((ReverseChannelConnection)connection).RunToolAsync(request, cts.Token));
        }
        finally
        {
            cts.Cancel();
        }
    }
}
