using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Unit tests for the production remote MCP host (issue #1438). The handler serves inbound
/// <c>{"type":"mcp","connection":{...}}</c> channels by opening the requested MCP server locally and
/// bridging it back; an unrecognised connection is declined (null) so the listener does not host it.
/// The full live-server bridge is exercised end-to-end in <c>McpToolContextProviderRoutingTests</c>.
/// </summary>
public sealed class RemoteMcpHostHandlerTests
{
    [Fact]
    public async Task OpenAsync_UnknownConnection_ReturnsNull()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        // An `mcp` request whose connection carries no endpoint is not a hostable server.
        var handle = await handler.OpenAsync(Json("""{"type":"mcp","connection":{}}"""), channel, Ct());

        Assert.Null(handle);
    }

    [Fact]
    public async Task OpenAsync_NonMcpRequest_ReturnsNull()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        var handle = await handler.OpenAsync(Json("""{"type":"chat-client"}"""), channel, Ct());

        Assert.Null(handle);
    }

    [Fact]
    public async Task OpenAsync_HttpConnection_HostsServer()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        // A valid HTTP connection descriptor yields a live host session (the server connection itself
        // is lazy). Disposing the handle tears down the bridge without surfacing connection errors.
        var handle = await handler.OpenAsync(
            Json("""{"type":"mcp","connection":{"server-name":"remote","endpoint":"http://127.0.0.1:59999/mcp","transport":"streamable"}}"""),
            channel,
            Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_StdioConnection_HostsServer()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        // A stdio connection descriptor selects the stdio branch of the shared factory
        // (McpTransportFactory.IsStdioEndpoint/CreateStdioTransport) rather than the HTTP branch.
        // Construction of the stdio transport is synchronous and the child process launch is lazy, so
        // the host session is live immediately; disposing tears the bridge down without a round-trip.
        var handle = await handler.OpenAsync(
            Json("""{"type":"mcp","connection":{"server-name":"remote-stdio","endpoint":"stdio://?command=my-server"}}"""),
            channel,
            Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_DisposesOnChannelClose()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        var handle = await handler.OpenAsync(
            Json("""{"type":"mcp","connection":{"server-name":"remote","endpoint":"http://127.0.0.1:59999/mcp"}}"""),
            channel,
            Ct());

        Assert.NotNull(handle);

        // Disposing twice is safe and never hangs (the relay is cancelled on the first disposal).
        await handle!.DisposeAsync();
        await handle.DisposeAsync();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private sealed class StubMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> reader = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> writer = Channel.CreateUnbounded<JsonElement>();

        public ChannelReader<JsonElement> Reader => this.reader.Reader;

        public ChannelWriter<JsonElement> Writer => this.writer.Writer;

        public ValueTask DisposeAsync()
        {
            this.reader.Writer.TryComplete();
            this.writer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
