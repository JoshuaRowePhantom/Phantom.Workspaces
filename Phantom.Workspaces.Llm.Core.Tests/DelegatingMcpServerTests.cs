using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class DelegatingMcpServerTests
{
    [Fact]
    public async Task ConnectAsync_ReturnsTransportFromUnderlyingClientTransport()
    {
        var (_, upstreamTransport) = InMemoryTransportPair.Create();
        IClientTransport server = new DelegatingMcpServer(new InMemoryClientTransport(upstreamTransport));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var transport = await server.ConnectAsync(cts.Token);

        Assert.Same(upstreamTransport, transport);
    }

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

    [Fact]
    public async Task RunAsync_PreconnectedTransport_IsNotConnectedAgain_AndClientOwnerIsDisposed()
    {
        var (downstreamClientTransport, proxyServerTransport) = InMemoryTransportPair.Create();
        var (proxyDelegatedTransport, delegatedServerTransport) = InMemoryTransportPair.Create();
        var owner = new OwnedClientTransport(proxyDelegatedTransport);
        var server = new DelegatingMcpServer(owner, proxyDelegatedTransport);
        using var cts = new CancellationTokenSource();

        var runTask = server.RunAsync(proxyServerTransport, cts.Token);
        await downstreamClientTransport.SendMessageAsync(
            new JsonRpcNotification { Method = "ready" },
            CancellationToken.None);
        _ = await ReadMessageAsync(
            delegatedServerTransport.MessageReader,
            CancellationToken.None);

        cts.Cancel();
        await runTask;
        await server.DisposeAsync();

        Assert.Equal(0, owner.ConnectCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_SequentialReuse_DisposesEachRunTransportExactlyOnce()
    {
        var first = new RelayTransport(Channel.CreateUnbounded<JsonRpcMessage>().Reader);
        var second = new RelayTransport(Channel.CreateUnbounded<JsonRpcMessage>().Reader);
        var owner = new QueueClientTransport(first, second);
        await using var server = new DelegatingMcpServer(owner);

        await RunUntilIncomingEofAsync(server);
        await RunUntilIncomingEofAsync(server);

        Assert.Equal(2, owner.ConnectCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_RelayFault_CancelsAndJoinsPeerBeforePropagating(bool incomingToDelegated)
    {
        var failure = new IOException(incomingToDelegated ? "incoming fault" : "delegated fault");
        var blockingReader = new CancellationObservingReader();
        var source = Channel.CreateUnbounded<JsonRpcMessage>();
        source.Writer.TryWrite(new JsonRpcNotification { Method = "fault" });

        var incoming = incomingToDelegated
            ? new RelayTransport(source.Reader)
            : new RelayTransport(blockingReader, (_, _) => Task.FromException(failure));
        var delegated = incomingToDelegated
            ? new RelayTransport(blockingReader, (_, _) => Task.FromException(failure))
            : new RelayTransport(source.Reader);
        await using var server = new DelegatingMcpServer(new InMemoryClientTransport(delegated));

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => server.RunAsync(incoming, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.True(blockingReader.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Equal(1, delegated.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_DualRelayFault_PreservesPrimaryAndObservesSecondary(bool incomingToDelegated)
    {
        var primary = new IOException("primary relay");
        var secondary = new InvalidOperationException("secondary relay");
        var peerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingMessages = Channel.CreateUnbounded<JsonRpcMessage>();
        var delegatedMessages = Channel.CreateUnbounded<JsonRpcMessage>();
        incomingMessages.Writer.TryWrite(new JsonRpcNotification { Method = "incoming" });
        delegatedMessages.Writer.TryWrite(new JsonRpcNotification { Method = "delegated" });

        async Task PrimarySend(JsonRpcMessage _, CancellationToken cancellationToken)
        {
            await peerStarted.Task.WaitAsync(cancellationToken);
            throw primary;
        }

        async Task SecondarySend(JsonRpcMessage _, CancellationToken cancellationToken)
        {
            peerStarted.TrySetResult();
            var cancellation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancellation.TrySetResult());
            await cancellation.Task;
            peerCompleted.TrySetResult();
            throw secondary;
        }

        var incoming = new RelayTransport(
            incomingMessages.Reader,
            incomingToDelegated ? SecondarySend : PrimarySend);
        var delegated = new RelayTransport(
            delegatedMessages.Reader,
            incomingToDelegated ? PrimarySend : SecondarySend);
        await using var server = new DelegatingMcpServer(new InMemoryClientTransport(delegated));

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => server.RunAsync(incoming, CancellationToken.None));

        Assert.Same(primary, thrown);
        Assert.True(peerCompleted.Task.IsCompletedSuccessfully);
        Assert.Equal(1, delegated.DisposeCount);
    }

    private static async Task RunUntilIncomingEofAsync(DelegatingMcpServer server)
    {
        var incomingChannel = Channel.CreateUnbounded<JsonRpcMessage>();
        incomingChannel.Writer.TryComplete();
        var incoming = new RelayTransport(incomingChannel.Reader);
        await server.RunAsync(incoming, CancellationToken.None);
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

    private sealed class OwnedClientTransport(ITransport delegatedTransport)
        : IClientTransport, IAsyncDisposable
    {
        public int ConnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string Name => "owned";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            return Task.FromResult(delegatedTransport);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueClientTransport(params ITransport[] transports) : IClientTransport
    {
        private readonly Queue<ITransport> transports = new(transports);
        public int ConnectCount { get; private set; }
        public string Name => "queue";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            return Task.FromResult(this.transports.Dequeue());
        }
    }

    private sealed class RelayTransport(
        ChannelReader<JsonRpcMessage> reader,
        Func<JsonRpcMessage, CancellationToken, Task>? send = null) : ITransport
    {
        private int disposeCount;
        public int DisposeCount => Volatile.Read(ref this.disposeCount);
        public string? SessionId => null;
        public ChannelReader<JsonRpcMessage> MessageReader => reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            send?.Invoke(message, cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationObservingReader : ChannelReader<JsonRpcMessage>
    {
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool TryRead(out JsonRpcMessage item)
        {
            item = null!;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(
                () =>
                {
                    this.CancellationObserved.TrySetResult();
                    completion.TrySetCanceled(cancellationToken);
                });
            return new ValueTask<bool>(completion.Task);
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
