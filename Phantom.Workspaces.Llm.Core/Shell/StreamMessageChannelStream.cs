using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// A duplex <see cref="Stream"/> backed by an <see cref="IStreamMessageChannel"/>: the inverse of
/// <see cref="StreamFramedMessageChannel"/>, which adapts a byte stream into a channel. Reads drain the
/// payloads of inbound <see cref="StreamFrameKind.Data"/> frames; writes are sent as outbound
/// <see cref="StreamFrameKind.Data"/> frames. Out-of-band <see cref="StreamFrameKind.Control"/> frames
/// are demultiplexed off the same channel and routed to the supplied <paramref name="controlHandler"/>
/// rather than appearing in the byte stream, and control can be sent with <see cref="SendControlAsync"/>.
/// A background pump performs the receive-side demultiplexing; sends are serialized so a control frame
/// can never interleave with an in-flight data frame.
/// </summary>
public sealed class StreamMessageChannelStream : Stream
{
    private readonly IStreamMessageChannel channel;
    private readonly bool ownsChannel;
    private readonly Func<StreamControlMessage, ValueTask>? controlHandler;
    private readonly Pipe inbound = new();
    private readonly Stream inboundReader;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource pumpCancellation = new();
    private readonly Task pumpTask;
    private bool disposed;

    public StreamMessageChannelStream(
        IStreamMessageChannel channel,
        Func<StreamControlMessage, ValueTask>? controlHandler = null,
        bool ownsChannel = true)
    {
        ArgumentNullException.ThrowIfNull(channel);
        this.channel = channel;
        this.controlHandler = controlHandler;
        this.ownsChannel = ownsChannel;
        this.inboundReader = this.inbound.Reader.AsStream();
        this.pumpTask = Task.Run(() => this.PumpAsync(this.pumpCancellation.Token));
    }

    /// <summary>Completes when the receive pump stops, i.e. the channel closed or the stream disposed.</summary>
    public Task Completion => this.pumpTask;

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Sends an out-of-band control frame over the channel, serialized against data writes.</summary>
    public ValueTask SendControlAsync(StreamControlMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return this.SendFrameAsync(new StreamFrame(StreamFrameKind.Control, message.ToPayload()), cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.inboundReader.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer)
        => this.inboundReader.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.inboundReader.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => this.inboundReader.ReadAsync(buffer, cancellationToken);

    // WARNING: This synchronous override blocks the calling thread while awaiting channel write.
    // Callers should use WriteAsync when possible.
    public override void Write(byte[] buffer, int offset, int count)
        => this.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!buffer.IsEmpty)
        {
            await this.SendFrameAsync(new StreamFrame(StreamFrameKind.Data, buffer), cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        await this.pumpCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await this.pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (this.ownsChannel)
        {
            await this.channel.DisposeAsync().ConfigureAwait(false);
        }

        this.inboundReader.Dispose();
        this.pumpCancellation.Dispose();
        this.sendGate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        // Best-effort non-blocking disposal. Signal cancellation and mark as disposed without
        // awaiting the async teardown. Callers in async contexts should use DisposeAsync via
        // 'await using' for proper resource cleanup.
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            this.pumpCancellation.Cancel();
        }

        base.Dispose(disposing);
    }

    private async ValueTask SendFrameAsync(StreamFrame frame, CancellationToken cancellationToken)
    {
        await this.sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.channel.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.sendGate.Release();
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await this.channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (frame.Kind == StreamFrameKind.Data)
                {
                    if (!frame.Payload.IsEmpty)
                    {
                        await this.inbound.Writer.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (this.controlHandler is not null)
                {
                    await this.controlHandler(StreamControlMessage.FromPayload(frame.Payload)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this.inbound.Writer.Complete();
        }
    }
}
