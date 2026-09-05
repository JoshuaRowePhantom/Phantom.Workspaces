using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Phantom.Workspaces.Transport.Mcp;

namespace Phantom.Workspaces.Transport.Tests.Mcp;

/// <summary>
/// Verifies the M2 adapter (issue #1438) faithfully pumps MCP SDK JSON-RPC messages in both
/// directions over an <see cref="IMessageChannel"/>, and that disposal closes the channel — the
/// contract a remote-bound <c>McpToolContextProvider</c> relies on to speak MCP over a routed channel.
/// </summary>
public sealed class McpChannelClientTransportTests
{
    [Fact]
    public async Task Send_Receive_PumpsMessagesOverChannel()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        var channel = new FakeMessageChannel();
        await using var adapter = new McpChannelClientTransport(channel);

        var transport = await adapter.ConnectAsync(ct);

        // Outbound: an SDK JSON-RPC request sent through the transport is serialized onto the channel.
        await transport.SendMessageAsync(new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/list" }, ct);
        var outbound = await channel.TestOutbound.ReadAsync(ct);
        var outboundMessage = JsonSerializer.Deserialize<JsonRpcMessage>(outbound, McpJsonUtilities.DefaultOptions);
        var request = Assert.IsType<JsonRpcRequest>(outboundMessage);
        Assert.Equal("tools/list", request.Method);

        // Inbound: a JSON-RPC frame written to the channel surfaces as an SDK message on the reader.
        var inbound = JsonSerializer.SerializeToElement<JsonRpcMessage>(
            new JsonRpcNotification { Method = "notifications/initialized" },
            McpJsonUtilities.DefaultOptions);
        await channel.TestInbound.WriteAsync(inbound, ct);
        var received = await transport.MessageReader.ReadAsync(ct);
        var notification = Assert.IsType<JsonRpcNotification>(received);
        Assert.Equal("notifications/initialized", notification.Method);
    }

    [Fact]
    public async Task Dispose_ClosesUnderlyingChannel()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        var channel = new FakeMessageChannel();
        var adapter = new McpChannelClientTransport(channel);

        _ = await adapter.ConnectAsync(ct);
        await adapter.DisposeAsync();

        Assert.True(channel.Disposed);
    }

    private sealed class FakeMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> toTransport = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> fromTransport = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.fromTransport.Writer;

        public ChannelReader<JsonElement> Reader => this.toTransport.Reader;

        public ChannelWriter<JsonElement> TestInbound => this.toTransport.Writer;

        public ChannelReader<JsonElement> TestOutbound => this.fromTransport.Reader;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.toTransport.Writer.TryComplete();
            this.fromTransport.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
