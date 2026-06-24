using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// Adapts a duplex byte <see cref="Stream"/> carrier (a WebSocket message stream, the binary
/// reverse-frame variant, or an in-memory duplex pipe for tests) into an
/// <see cref="IStreamMessageChannel"/> using the binary shell wire encoding:
/// <c>[kind: 1 byte][payload length: 4 bytes big-endian][payload bytes]</c>. Writes are serialized so
/// concurrent <see cref="SendAsync"/> callers cannot interleave a frame's header and body.
/// </summary>
public sealed class StreamFramedMessageChannel : IStreamMessageChannel
{
    private const int HeaderLength = 5;

    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public StreamFramedMessageChannel(Stream stream, bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.ownsStream = ownsStream;
    }

    public async Task SendAsync(StreamFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var header = new byte[HeaderLength];
        header[0] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), (uint)frame.Payload.Length);

        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!frame.Payload.IsEmpty)
            {
                await this.stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
            }

            await this.stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        if (!await this.TryReadHeaderAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = (StreamFrameKind)header[0];
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        if (length == 0)
        {
            return new StreamFrame(kind, ReadOnlyMemory<byte>.Empty);
        }

        var payload = new byte[length];
        await this.stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new StreamFrame(kind, payload);
    }

    public async ValueTask DisposeAsync()
    {
        this.writeGate.Dispose();
        if (this.ownsStream)
        {
            await this.stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Reads a full frame header, returning false on a clean end-of-stream (zero bytes available at a
    // frame boundary) and throwing on a truncated header (end-of-stream mid-header).
    private async Task<bool> TryReadHeaderAsync(byte[] header, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < header.Length)
        {
            var bytes = await this.stream
                .ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (bytes == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The shell transport ended in the middle of a frame header.");
            }

            read += bytes;
        }

        return true;
    }
}
