using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class HttpReverseMessageChannelTests
{
    // Creates two HttpReverseMessageChannel instances connected back-to-back via Pipe pairs.
    // A frame written on the client end arrives at the server end, and vice versa.
    private static (IReverseMessageChannel ServerEnd, IReverseMessageChannel ClientEnd) CreatePipedPair()
    {
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();

        var serverChannel = new HttpReverseMessageChannel(clientToServer.Reader, serverToClient.Writer);
        var clientChannel = new HttpReverseMessageChannel(serverToClient.Reader, clientToServer.Writer);

        return (serverChannel, clientChannel);
    }

    [Fact]
    public async Task SendAsync_WritesNdjsonLine()
    {
        var pipe = new Pipe();
        // writer → pipe → reader; channel writes to the pipe writer
        await using var channel = new HttpReverseMessageChannel(PipeReader.Create(System.IO.Stream.Null), pipe.Writer);

        var frame = new ReverseFrame { Type = ReverseFrame.Types.Register, ClientInstanceId = "computer-a" };
        await channel.SendAsync(frame, CancellationToken.None);

        // Complete writing so we can read everything
        await pipe.Writer.CompleteAsync();

        var result = await pipe.Reader.ReadAsync();
        var raw = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        Assert.EndsWith("\n", raw);
        var deserialized = JsonSerializer.Deserialize<ReverseFrame>(raw.TrimEnd('\n'), AIJsonUtilities.DefaultOptions)!;
        Assert.Equal(ReverseFrame.Types.Register, deserialized.Type);
        Assert.Equal("computer-a", deserialized.ClientInstanceId);
    }

    [Fact]
    public async Task ReceiveAsync_DeserializesNdjsonLine()
    {
        var pipe = new Pipe();
        await using var channel = new HttpReverseMessageChannel(pipe.Reader, PipeWriter.Create(System.IO.Stream.Null));

        // NDJSON requires compact (non-indented) JSON — one frame per line.
        var compactOptions = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        var frame = new ReverseFrame { Type = ReverseFrame.Types.Execute, CorrelationId = "corr-1" };
        var json = JsonSerializer.Serialize(frame, compactOptions);
        var lineBytes = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.Writer.WriteAsync(lineBytes);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var received = await channel.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal(ReverseFrame.Types.Execute, received!.Type);
        Assert.Equal("corr-1", received.CorrelationId);
    }

    [Fact]
    public async Task ChannelPair_FullRoundTrip()
    {
        var (serverEnd, clientEnd) = CreatePipedPair();
        await using var _ = serverEnd;
        await using var __ = clientEnd;

        var sentFrame = new ReverseFrame
        {
            Type = ReverseFrame.Types.Complete,
            CorrelationId = "round-trip-1",
            Error = new ReverseExecutionError("test-error", "something went wrong"),
        };

        await clientEnd.SendAsync(sentFrame, CancellationToken.None);
        var received = await serverEnd.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal(ReverseFrame.Types.Complete, received!.Type);
        Assert.Equal("round-trip-1", received.CorrelationId);
        Assert.Equal("test-error", received.Error!.Code);
        Assert.Equal("something went wrong", received.Error.Message);
    }

    [Fact]
    public async Task ReceiveAsync_ReturnsNull_WhenPipeCompleted()
    {
        var pipe = new Pipe();
        await using var channel = new HttpReverseMessageChannel(pipe.Reader, PipeWriter.Create(System.IO.Stream.Null));

        // Complete the reader side with no data
        await pipe.Writer.CompleteAsync();

        var result = await channel.ReceiveAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AcceptorWithHttpChannel_RegistersConnection()
    {
        var (serverEnd, clientEnd) = CreatePipedPair();

        var registry = new ReverseExecutionRegistry();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ConnectionsChanged += (_, _) =>
        {
            if (registry.IsConnected("computer-http"))
            {
                connected.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource();

        var acceptor = new ReverseConnectionAcceptor(registry);
        _ = acceptor.AcceptAsync(serverEnd, cts.Token);

        await clientEnd.SendAsync(
            new ReverseFrame { Type = ReverseFrame.Types.Register, ClientInstanceId = "computer-http" },
            cts.Token);

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.IsConnected("computer-http"));

        cts.Cancel();
        await clientEnd.DisposeAsync();
        await serverEnd.DisposeAsync();
    }

    [Fact]
    public async Task ForEndpointHttp_EndToEnd_ExecutesRequest()
    {
        // Arrange: build a minimal in-memory server using UseTestServer to avoid real TCP
        // sockets and HTTP/2 cleartext (h2c) negotiation restrictions.
        var registry = new ReverseExecutionRegistry();
        var handler = new StubChunkHandler("Hello, ", "world");

        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseTestServer();
        var app = appBuilder.Build();
        app.MapReverseEndpoints(registry);
        await app.StartAsync();
        await using var _ = app;

        // Wait for the client to connect and register
        var clientConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ConnectionsChanged += (_, _) =>
        {
            if (registry.IsConnected("client-http"))
            {
                clientConnected.TrySetResult();
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Route via the in-memory test server handler to avoid h2c restrictions.
        var clientHost = ReverseExecutionClientHost.ForEndpointHttp(
            "http://localhost",
            "client-http",
            handler,
            httpMessageHandler: app.GetTestServer().CreateHandler());

        var runTask = clientHost.RunAsync(cts.Token);

        await clientConnected.Task.WaitAsync(cts.Token);
        Assert.True(registry.TryGetConnection("client-http", out var connection));

        // Act: execute a request through the registered connection
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in connection!.ExecuteAsync(
            new RemoteAgentRequest { AgentDefinitionJson = "{}", Messages = [new ChatMessage(ChatRole.User, "hi")] },
            cts.Token))
        {
            updates.Add(update);
        }

        Assert.Equal("Hello, world", string.Concat(updates.ConvertAll(static u => u.Text ?? "")));

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    private sealed class StubChunkHandler : IReverseExecutionHandler
    {
        private readonly string[] chunks;

        public StubChunkHandler(params string[] chunks) => this.chunks = chunks;

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in this.chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public Task HandleStreamAsync(
            string streamKind,
            string openPayloadJson,
            Phantom.Workspaces.Llm.Shell.IStreamMessageChannel channel,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
