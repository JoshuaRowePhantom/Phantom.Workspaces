using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Local;

internal sealed class LocalDuplexStream : Stream
{
    private readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
    private byte[]? current;
    private int currentOffset;
    private LocalDuplexStream? peer;
    private Exception? exception;
    private bool disposed;

    public override bool CanRead => !this.disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !this.disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public static (LocalDuplexStream client, LocalDuplexStream server) CreatePair()
    {
        var client = new LocalDuplexStream();
        var server = new LocalDuplexStream();
        client.peer = server;
        server.peer = client;
        return (client, server);
    }

    public void SetException(Exception ex)
    {
        this.exception = ex;
        this.inbound.Writer.TryComplete(ex);
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.exception is not null)
        {
            throw this.exception;
        }

        while (this.current is null || this.currentOffset >= this.current.Length)
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

        var length = Math.Min(buffer.Length, this.current.Length - this.currentOffset);
        this.current.AsMemory(this.currentOffset, length).CopyTo(buffer);
        this.currentOffset += length;
        return length;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(LocalDuplexStream));
        }

        if (this.peer is null)
        {
            throw new InvalidOperationException("Stream is not connected.");
        }

        return this.peer.inbound.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            this.disposed = true;
            this.inbound.Writer.TryComplete();
            this.peer?.inbound.Writer.TryComplete();
        }

        base.Dispose(disposing);
    }
}
