using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class DelegatingMcpServerTests
{
    [Fact]
    public async Task RunAsync_ForwardsIncomingClientRequestsToDelegatedTransport()
    {
        var (downstreamClientTransport, proxyServerTransport) = InMemoryTransportPair.Create();
        var (proxyDelegatedTransport, delegatedServerTransport) = InMemoryTransportPair.Create();
        var server = new DelegatingMcpServer(new InMemoryClientTransport(proxyDelegatedTransport));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = server.RunAsync(proxyServerTransport, cts.Token);

        var request = new JsonRpcRequest
        {
            Id = new RequestId(17L),
            Method = "tools/list",
            Params = JsonNode.Parse("""{"cursor":"abc"}"""),
        };
        await downstreamClientTransport.SendMessageAsync(request, cts.Token);

        var forwardedMessage = await ReadMessageAsync(delegatedServerTransport.MessageReader, cts.Token);
        var forwardedRequest = Assert.IsType<JsonRpcRequest>(forwardedMessage);
        Assert.Equal("tools/list", forwardedRequest.Method);
        Assert.Equal(new RequestId(17L), forwardedRequest.Id);

        cts.Cancel();
        await runTask;
    }

    [Fact]
    public async Task RunAsync_ForwardsDelegatedNotificationsAndResponsesToIncomingClient()
    {
        var (downstreamClientTransport, proxyServerTransport) = InMemoryTransportPair.Create();
        var (proxyDelegatedTransport, delegatedServerTransport) = InMemoryTransportPair.Create();
        var server = new DelegatingMcpServer(new InMemoryClientTransport(proxyDelegatedTransport));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = server.RunAsync(proxyServerTransport, cts.Token);

        await delegatedServerTransport.SendMessageAsync(
            new JsonRpcNotification
            {
                Method = "notifications/message",
                Params = JsonNode.Parse("""{"level":"info"}"""),
            },
            cts.Token);
        await delegatedServerTransport.SendMessageAsync(
            new JsonRpcResponse
            {
                Id = new RequestId("req-1"),
                Result = JsonNode.Parse("""{"ok":true}"""),
            },
            cts.Token);

        var notification = Assert.IsType<JsonRpcNotification>(
            await ReadMessageAsync(downstreamClientTransport.MessageReader, cts.Token));
        Assert.Equal("notifications/message", notification.Method);

        var response = Assert.IsType<JsonRpcResponse>(
            await ReadMessageAsync(downstreamClientTransport.MessageReader, cts.Token));
        Assert.Equal(new RequestId("req-1"), response.Id);

        cts.Cancel();
        await runTask;
    }

    private static async Task<JsonRpcMessage> ReadMessageAsync(
        ChannelReader<JsonRpcMessage> reader,
        CancellationToken cancellationToken)
    {
        var message = await reader.ReadAsync(cancellationToken);
        return message;
    }

    private sealed class InMemoryClientTransport(
        ITransport delegatedTransport) : IClientTransport
    {
        public string Name => "in-memory";

        public Task<ITransport> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(delegatedTransport);
        }
    }

    private sealed class InMemoryTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> incoming = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly Func<JsonRpcMessage, CancellationToken, Task> sendAsync;

        public InMemoryTransport(
            Func<JsonRpcMessage, CancellationToken, Task> sendAsync)
        {
            this.sendAsync = sendAsync;
        }

        public string? SessionId => "in-memory-session";

        public ChannelReader<JsonRpcMessage> MessageReader => this.incoming.Reader;

        public Task SendMessageAsync(
            JsonRpcMessage message,
            CancellationToken cancellationToken = default)
        {
            return this.sendAsync(message, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            this.incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public Task ReceiveAsync(
            JsonRpcMessage message,
            CancellationToken cancellationToken = default)
        {
            return this.incoming.Writer.WriteAsync(message, cancellationToken).AsTask();
        }
    }

    private static class InMemoryTransportPair
    {
        public static (InMemoryTransport First, InMemoryTransport Second) Create()
        {
            InMemoryTransport? first = null;
            InMemoryTransport? second = null;
            first = new InMemoryTransport((message, cancellationToken) => second!.ReceiveAsync(message, cancellationToken));
            second = new InMemoryTransport((message, cancellationToken) => first.ReceiveAsync(message, cancellationToken));
            return (first, second);
        }
    }
}
