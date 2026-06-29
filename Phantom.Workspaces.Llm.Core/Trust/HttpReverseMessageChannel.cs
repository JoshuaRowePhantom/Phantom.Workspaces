using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using PipelineReadResult = System.IO.Pipelines.ReadResult;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An <see cref="IReverseMessageChannel"/> over an HTTP request/response pair. Frames are serialised
/// as newline-delimited JSON (NDJSON) using the Microsoft.Extensions.AI serialisation contracts.
/// The inbound direction reads from <paramref name="reader"/> (the HTTP response body on the client,
/// or the request body on the server); the outbound direction writes to <paramref name="writer"/>
/// (the request body on the client, or the response body on the server).
/// Concurrent sends are serialised; a single reader is expected.
/// </summary>
public sealed class HttpReverseMessageChannel : IReverseMessageChannel
{
    // NDJSON requires WriteIndented = false so each frame serialises as a single line.
    // AIJsonUtilities.DefaultOptions uses WriteIndented = true; create a compact copy.
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
    {
        WriteIndented = false,
    };
    private static readonly ReadOnlyMemory<byte> NewLine = new byte[] { (byte)'\n' };

    private readonly PipeReader reader;
    private readonly PipeWriter writer;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly IDisposable? owned;

    /// <summary>Creates a channel that reads from <paramref name="reader"/> and writes to <paramref name="writer"/>.</summary>
    public HttpReverseMessageChannel(PipeReader reader, PipeWriter writer)
        : this(reader, writer, owned: null)
    {
    }

    internal HttpReverseMessageChannel(PipeReader reader, PipeWriter writer, IDisposable? owned)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.owned = owned;
    }

    public async Task SendAsync(ReverseFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, SerializerOptions);

        await this.sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.writer.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await this.writer.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await this.writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    public async Task<ReverseFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PipelineReadResult result;
            try
            {
                result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return null;
            }

            if (TryReadLine(result.Buffer, out var line, out var consumed))
            {
                // Deserialize before AdvanceTo — the sequence memory becomes invalid after the advance.
                var frame = DeserializeLine(line);
                this.reader.AdvanceTo(consumed);
                return frame;
            }

            this.reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            if (result.IsCompleted)
            {
                return null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }

        try
        {
            await this.reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }

        this.sendLock.Dispose();
        this.owned?.Dispose();
    }

    private static ReverseFrame? DeserializeLine(ReadOnlySequence<byte> line)
    {
        var buffer = new ArrayBufferWriter<byte>((int)line.Length);
        foreach (var segment in line)
        {
            buffer.Write(segment.Span);
        }

        return JsonSerializer.Deserialize<ReverseFrame>(buffer.WrittenSpan, SerializerOptions);
    }

    private static bool TryReadLine(
        ReadOnlySequence<byte> buffer,
        out ReadOnlySequence<byte> line,
        out SequencePosition consumed)
    {
        var sequenceReader = new SequenceReader<byte>(buffer);
        if (sequenceReader.TryReadTo(out line, (byte)'\n', advancePastDelimiter: true))
        {
            consumed = sequenceReader.Position;
            return true;
        }

        line = default;
        consumed = default;
        return false;
    }
}
