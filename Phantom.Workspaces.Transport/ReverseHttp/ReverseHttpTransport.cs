using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpTransport(IMessageChannel registrationChannel) : ITransport
{
    public async Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
    {
        var channelId = Guid.NewGuid().ToString("D");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "channel-open",
            channelId,
            request = JsonSerializer.Deserialize<JsonElement>(request.GetRawText()),
        }));
        await registrationChannel.Writer.WriteAsync(document.RootElement.Clone(), ct).ConfigureAwait(false);
        return new ReverseHttpMessageChannel(registrationChannel, channelId);
    }

    public async Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
    {
        var streamId = Guid.NewGuid().ToString("D");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "stream-open",
            streamId,
            request = JsonSerializer.Deserialize<JsonElement>(request.GetRawText()),
        }));
        await registrationChannel.Writer.WriteAsync(document.RootElement.Clone(), ct).ConfigureAwait(false);
        return new MemoryStream();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ReverseHttpMessageChannel : IMessageChannel
    {
        private readonly IMessageChannel registrationChannel;
        private readonly string channelId;
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();

        public ReverseHttpMessageChannel(IMessageChannel registrationChannel, string channelId)
        {
            this.registrationChannel = registrationChannel;
            this.channelId = channelId;
            this.Writer = new MultiplexingWriter(registrationChannel.Writer, channelId);
        }

        public ChannelWriter<JsonElement> Writer { get; }

        public ChannelReader<JsonElement> Reader => this.incoming.Reader;

        public ValueTask DisposeAsync()
        {
            this.incoming.Writer.TryComplete();
            using var document = JsonDocument.Parse($$"""{"type":"channel-close","channelId":"{{this.channelId}}"}""");
            return this.registrationChannel.Writer.WriteAsync(document.RootElement.Clone());
        }
    }

    private sealed class MultiplexingWriter(ChannelWriter<JsonElement> inner, string channelId) : ChannelWriter<JsonElement>
    {
        public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);

        public override bool TryWrite(JsonElement item)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "channel-message",
                channelId,
                payload = JsonSerializer.Deserialize<JsonElement>(item.GetRawText()),
            }));
            return inner.TryWrite(document.RootElement.Clone());
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => inner.WaitToWriteAsync(cancellationToken);
    }
}
