using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Http;

internal sealed class ServerTransportStream(ServerHttpTransport transport, string streamId) : Stream
{
    private readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
    private byte[] current = [];
    private int currentOffset;
    private bool completed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public ValueTask ReceiveAsync(byte[] payload, CancellationToken cancellationToken)
        => this.inbound.Writer.WriteAsync(payload, cancellationToken);

    public void Complete()
    {
        this.completed = true;
        this.inbound.Writer.TryComplete();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (this.currentOffset >= this.current.Length)
        {
            if (!await this.inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (this.inbound.Reader.TryRead(out var next))
            {
                this.current = next;
                this.currentOffset = 0;
            }
        }

        var copied = Math.Min(buffer.Length, this.current.Length - this.currentOffset);
        this.current.AsMemory(this.currentOffset, copied).CopyTo(buffer);
        this.currentOffset += copied;
        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => await transport.SendStreamDataAsync(streamId, buffer, cancellationToken).ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.completed)
        {
            this.Complete();
            _ = transport.SendStreamCloseAsync(streamId, CancellationToken.None);
        }

        base.Dispose(disposing);
    }
}
