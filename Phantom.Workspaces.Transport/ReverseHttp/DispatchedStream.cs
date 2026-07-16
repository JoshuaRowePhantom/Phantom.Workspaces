using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.ReverseHttp;

/// <summary>
/// A stream surfaced to a listener for a relayed <c>stream-open</c>. Outbound writes are wrapped as
/// multiplexed <c>stream-data</c> frames on the shared registration channel; inbound bytes are fed
/// by the dispatcher from demultiplexed <c>stream-data</c> frames.
/// </summary>
internal sealed class DispatchedStream : Stream
{
    private readonly ChannelWriter<JsonElement> outbound;
    private readonly string streamId;
    private readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
    private ReadOnlyMemory<byte> pending;

    public DispatchedStream(ChannelWriter<JsonElement> outbound, string streamId)
    {
        this.outbound = outbound;
        this.streamId = streamId;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void DeliverIncoming(ReadOnlySpan<byte> data) => this.inbound.Writer.TryWrite(data.ToArray());

    public void CompleteIncoming() => this.inbound.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.pending.IsEmpty)
        {
            if (!await this.inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            this.inbound.Reader.TryRead(out var next);
            this.pending = next;
        }

        var count = Math.Min(this.pending.Length, buffer.Length);
        this.pending.Span[..count].CopyTo(buffer.Span);
        this.pending = this.pending[count..];
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "stream-data",
            streamId = this.streamId,
            data = Convert.ToBase64String(buffer.Span),
        }));
        await this.outbound.WriteAsync(document.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("DispatchedStream supports asynchronous reads only.");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("DispatchedStream supports asynchronous writes only.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.inbound.Writer.TryComplete();
        }

        base.Dispose(disposing);
    }
}
