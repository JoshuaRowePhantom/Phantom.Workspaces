using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpTransportTests
{
    [Fact]
    public async Task ReverseHttpTransport_RelayedRoundTrip_ReceivesResponseFrames()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);
        var channel = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), Ct());

        var open = await underlying.Outbound.ReadAsync(Ct());
        var channelId = open.GetProperty("channelId").GetString()!;
        Assert.Equal("channel-open", open.GetProperty("type").GetString());

        await underlying.DeliverInbound(ChannelMessage(channelId, """{"type":"streaming-update","content":"pong"}"""));
        var received = await channel.Reader.ReadAsync(Ct());

        Assert.Equal("streaming-update", received.GetProperty("type").GetString());
        Assert.Equal("pong", received.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ReverseHttpTransport_InboundMessage_RoutedToOriginatingChannel()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);
        var channelA = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), Ct());
        var channelB = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), Ct());

        var openA = await underlying.Outbound.ReadAsync(Ct());
        var openB = await underlying.Outbound.ReadAsync(Ct());
        var idA = openA.GetProperty("channelId").GetString()!;
        var idB = openB.GetProperty("channelId").GetString()!;
        Assert.NotEqual(idA, idB);

        await underlying.DeliverInbound(ChannelMessage(idA, """{"marker":"for-a"}"""));
        var receivedA = await channelA.Reader.ReadAsync(Ct());

        Assert.Equal("for-a", receivedA.GetProperty("marker").GetString());
        Assert.False(channelB.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReverseHttpTransport_ChannelClose_CompletesOriginatingReader()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);
        var channel = await transport.ConnectToMessageChannelAsync(Json("""{"type":"chat-client"}"""), Ct());
        var open = await underlying.Outbound.ReadAsync(Ct());
        var channelId = open.GetProperty("channelId").GetString();

        await underlying.DeliverInbound(Json($$"""{"type":"channel-close","channelId":"{{channelId}}"}"""));

        await channel.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(channel.Reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReverseHttpTransport_RelayAck_EstablishesRelay()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);

        await underlying.DeliverInbound(Json("""{"type":"channel-open-ack"}"""));

        await transport.WaitForRelayEstablishedAsync(Ct());
    }

    [Fact]
    public async Task ReverseHttpTransport_ChannelOpenError_WaitThrowsTransportException()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);

        await underlying.DeliverInbound(Json("""{"type":"channel-open-error","error-code":"not-registered","message":"nope"}"""));

        var ex = await Assert.ThrowsAsync<TransportException>(async () => await transport.WaitForRelayEstablishedAsync(Ct()));
        Assert.Equal("nope", ex.Message);
    }

    [Fact]
    public async Task ReverseHttpTransport_RelayChannelClosed_WaitThrowsTransportException()
    {
        var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);

        await underlying.DisposeAsync();

        await Assert.ThrowsAsync<TransportException>(async () => await transport.WaitForRelayEstablishedAsync(Ct()));
    }

    [Fact]
    public async Task ReverseHttpTransport_RelayedStream_RoundTripsDataFrames()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);
        var stream = await transport.ConnectToStreamAsync(Json("""{"type":"shell","command":"echo"}"""), Ct());

        var open = await underlying.Outbound.ReadAsync(Ct());
        var streamId = open.GetProperty("streamId").GetString()!;
        Assert.Equal("stream-open", open.GetProperty("type").GetString());

        // Outbound write is multiplexed as a stream-data frame carrying base64 bytes.
        var outboundBytes = new byte[] { 1, 2, 3, 4 };
        await stream.WriteAsync(outboundBytes, Ct());
        var data = await underlying.Outbound.ReadAsync(Ct());
        Assert.Equal("stream-data", data.GetProperty("type").GetString());
        Assert.Equal(streamId, data.GetProperty("streamId").GetString());
        Assert.Equal(outboundBytes, Convert.FromBase64String(data.GetProperty("data").GetString()!));

        // Inbound stream-data is demuxed by streamId and surfaced on the stream reader.
        var inboundBytes = new byte[] { 9, 8, 7 };
        await underlying.DeliverInbound(StreamData(streamId, inboundBytes));
        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer, Ct());
        Assert.Equal(inboundBytes, buffer[..read]);
    }

    [Fact]
    public async Task ReverseHttpTransport_StreamClose_CompletesOriginatingStream()
    {
        await using var underlying = new UnderlyingChannel();
        await using var transport = new ReverseHttpTransport(underlying);
        var stream = await transport.ConnectToStreamAsync(Json("""{"type":"shell","command":"echo"}"""), Ct());
        var open = await underlying.Outbound.ReadAsync(Ct());
        var streamId = open.GetProperty("streamId").GetString();

        await underlying.DeliverInbound(Json($$"""{"type":"stream-close","streamId":"{{streamId}}"}"""));

        var read = await stream.ReadAsync(new byte[8], Ct());
        Assert.Equal(0, read);
    }

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static JsonElement StreamData(string streamId, byte[] data)
        => JsonSerializer.SerializeToElement(new
        {
            type = "stream-data",
            streamId,
            data = Convert.ToBase64String(data),
        });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ChannelMessage(string channelId, string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return JsonSerializer.SerializeToElement(new
        {
            type = "channel-message",
            channelId,
            payload = payload.RootElement.Clone(),
        });
    }

    // A test double for the shared registration/relay channel: the transport under test writes its
    // multiplexed frames to Outbound (which the test inspects) and reads inbound frames the test injects.
    private sealed class UnderlyingChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ChannelReader<JsonElement> Outbound => this.outbound.Reader;

        public ValueTask DeliverInbound(JsonElement frame) => this.inbound.Writer.WriteAsync(frame);

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            this.outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
