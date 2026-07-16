using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.ReverseHttp;

/// <summary>
/// Wraps writes to a logical channel as multiplexed <c>channel-message</c> frames carrying the
/// owning channel identifier, so many logical channels can share a single underlying transport
/// channel (a reverse-HTTP registration or relay channel).
/// </summary>
internal sealed class MultiplexingChannelWriter(ChannelWriter<JsonElement> inner, string channelId) : ChannelWriter<JsonElement>
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

    public override ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "channel-message",
            channelId,
            payload = JsonSerializer.Deserialize<JsonElement>(item.GetRawText()),
        }));
        return inner.WriteAsync(document.RootElement.Clone(), cancellationToken);
    }
}
