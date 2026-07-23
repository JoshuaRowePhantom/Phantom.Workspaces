using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Http;

internal sealed class HttpTransportStream(HttpTransport transport, string streamId) : Stream
{
    private readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
    private byte[] current = [];
    private int currentOffset;
    private Exception? fault;
    private int disposed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (this.currentOffset >= this.current.Length)
        {
            if (this.fault is { } ex)
            {
                throw ex;
            }

            if (!await this.inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (this.fault is { } faultAfter)
                {
                    throw faultAfter;
                }

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

    internal ValueTask ReceiveAsync(byte[] payload, CancellationToken cancellationToken)
        => this.inbound.Writer.WriteAsync(payload, cancellationToken);

    internal void Complete()
    {
        this.inbound.Writer.TryComplete();
    }

    internal void Fault(Exception exception)
    {
        this.fault = exception;
        this.inbound.Writer.TryComplete(exception);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            this.inbound.Writer.TryComplete();
            transport.NotifyStreamDisposed(streamId);
        }

        base.Dispose(disposing);
    }
}
