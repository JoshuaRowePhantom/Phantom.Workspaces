using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Http;

internal sealed class HttpTransportChannel(HttpTransport transport, string channelId) : IMessageChannel
{
    private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();

    public ChannelWriter<JsonElement> Writer => new ForwardingWriter(transport, channelId);

    public ChannelReader<JsonElement> Reader => this.inbound.Reader;

    public ValueTask ReceiveAsync(JsonElement payload) => this.inbound.Writer.WriteAsync(payload.Clone());

    public void Complete(Exception? exception = null) => this.inbound.Writer.TryComplete(exception);

    public async ValueTask DisposeAsync()
    {
        this.Complete();
        await transport.SendChannelCloseAsync(channelId, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class ForwardingWriter(HttpTransport transport, string channelId) : ChannelWriter<JsonElement>
    {
        public override bool TryWrite(JsonElement item)
        {
            _ = this.WriteAsync(item);
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public override async ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
            => await transport.SendChannelMessageAsync(channelId, item, cancellationToken).ConfigureAwait(false);
    }
}
