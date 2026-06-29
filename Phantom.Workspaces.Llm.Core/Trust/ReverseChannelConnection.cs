using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// The server-side <see cref="IReverseConnection"/> over a duplex <see cref="IReverseMessageChannel"/>.
/// It sends <c>execute</c> frames for each turn and multiplexes the <c>update</c>/<c>complete</c>
/// frames streamed back, correlating them by id. Call <see cref="Start"/> to begin the read loop and
/// await <see cref="Completion"/> for the connection's lifetime.
/// </summary>
public sealed class ReverseChannelConnection : IReverseConnection, IAsyncDisposable
{
    private readonly IReverseMessageChannel channel;
    private readonly ConcurrentDictionary<string, Turn> turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamRelay> streams = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource closed = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? readLoop;

    public ReverseChannelConnection(
        IReverseMessageChannel channel,
        string clientInstanceId,
        DateTimeOffset connectedAt,
        string? announcedEndpoint = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        this.ClientInstanceId = clientInstanceId;
        this.ConnectedAt = connectedAt;
        this.AnnouncedEndpoint = announcedEndpoint;
    }

    public string ClientInstanceId { get; }

    public DateTimeOffset ConnectedAt { get; }

    /// <summary>
    /// The absolute base URL of C's own Phantom.Workspaces HTTP endpoint, as announced in the
    /// <c>register</c> frame. <see langword="null"/> when C did not announce an endpoint.
    /// </summary>
    public string? AnnouncedEndpoint { get; }

    public int InFlightCount => this.turns.Count;

    /// <summary>Completes when the connection's read loop ends (the channel closed).</summary>
    public Task Completion => this.completion.Task;

    /// <summary>Starts the background read loop that routes streamed frames to in-flight turns.</summary>
    public void Start()
    {
        this.readLoop ??= Task.Run(this.ReadLoopAsync);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
        RemoteAgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = Guid.NewGuid().ToString("N");
        var turn = new Turn();
        this.turns[correlationId] = turn;
        try
        {
            await this.channel.SendAsync(
                new ReverseFrame { Type = ReverseFrame.Types.Execute, CorrelationId = correlationId, Request = request },
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var update = await turn.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (update is null)
                {
                    break;
                }

                yield return update;
            }

            if (turn.Error is { } error)
            {
                throw new InvalidOperationException($"Reverse execution failed ({error.Code}): {error.Message}");
            }
        }
        finally
        {
            this.turns.TryRemove(correlationId, out _);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = Guid.NewGuid().ToString("N");
        var relay = new StreamRelay(this.channel, correlationId);
        this.streams[correlationId] = relay;

        try
        {
            await this.channel.SendAsync(
                new ReverseFrame
                {
                    Type = ReverseFrame.Types.OpenStream,
                    CorrelationId = correlationId,
                    StreamKind = request.StreamKind,
                    StreamOpenPayload = JsonSerializer.Serialize(request.OpenPayload),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            this.streams.TryRemove(correlationId, out _);
            await relay.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new StreamMessageChannelStream(relay);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await this.channel.ReceiveAsync(this.closed.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Type)
                {
                    case ReverseFrame.Types.Update
                        when frame.CorrelationId is { } updateId
                        && frame.Update is { } update
                        && this.turns.TryGetValue(updateId, out var updateTurn):
                        updateTurn.Write(update);
                        break;

                    case ReverseFrame.Types.Complete
                        when frame.CorrelationId is { } completeId
                        && this.turns.TryGetValue(completeId, out var completeTurn):
                        completeTurn.Complete(frame.Error);
                        break;

                    case ReverseFrame.Types.StreamData
                        when frame.CorrelationId is { } dataId
                        && this.streams.TryGetValue(dataId, out var dataRelay):
                        dataRelay.Deliver(new StreamFrame(
                            (StreamFrameKind)(frame.StreamFrameKindByte ?? 0),
                            frame.StreamData ?? Array.Empty<byte>()));
                        break;

                    case ReverseFrame.Types.StreamClose
                        when frame.CorrelationId is { } closeId
                        && this.streams.TryRemove(closeId, out var closeRelay):
                        closeRelay.CompleteInbound();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // The connection closed: fault any in-flight turns so callers fail fast.
            foreach (var turn in this.turns.Values)
            {
                turn.Complete(new ReverseExecutionError("disconnected", "The reverse connection closed."));
            }

            // Close any open stream relays so callers see EOF.
            foreach (var relay in this.streams.Values)
            {
                relay.CompleteInbound();
            }

            this.completion.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.closed.Cancel();
        if (this.readLoop is not null)
        {
            try
            {
                await this.readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await this.channel.DisposeAsync().ConfigureAwait(false);
        this.closed.Dispose();
    }

    /// <summary>Buffers the streamed updates and terminal error for a single in-flight turn.</summary>
    private sealed class Turn
    {
        private readonly Channel<ChatResponseUpdate> updates =
            Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions { SingleReader = true });

        public ReverseExecutionError? Error { get; private set; }

        public void Write(ChatResponseUpdate update) => this.updates.Writer.TryWrite(update);

        public void Complete(ReverseExecutionError? error)
        {
            this.Error = error;
            this.updates.Writer.TryComplete();
        }

        public async Task<ChatResponseUpdate?> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (await this.updates.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                    && this.updates.Reader.TryRead(out var update))
                {
                    return update;
                }

                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// An <see cref="IStreamMessageChannel"/> that relays <see cref="StreamFrame"/>s over the reverse
    /// channel as <c>stream-data</c> / <c>stream-close</c> <see cref="ReverseFrame"/>s. The server
    /// registers one relay per open stream; the read loop delivers inbound frames to
    /// <see cref="Deliver"/> and the caller uses <see cref="ReceiveAsync"/> to consume them.
    /// </summary>
    private sealed class StreamRelay : IStreamMessageChannel
    {
        private readonly IReverseMessageChannel reverseChannel;
        private readonly string correlationId;
        private readonly Channel<StreamFrame> inbound =
            Channel.CreateUnbounded<StreamFrame>(new UnboundedChannelOptions { SingleReader = true });

        public StreamRelay(IReverseMessageChannel reverseChannel, string correlationId)
        {
            this.reverseChannel = reverseChannel;
            this.correlationId = correlationId;
        }

        /// <summary>Delivers an inbound frame received by the read loop.</summary>
        public void Deliver(StreamFrame frame) => this.inbound.Writer.TryWrite(frame);

        /// <summary>Signals that no more inbound frames will be delivered (stream closed by C).</summary>
        public void CompleteInbound() => this.inbound.Writer.TryComplete();

        public Task SendAsync(StreamFrame frame, CancellationToken cancellationToken)
        {
            return this.reverseChannel.SendAsync(
                new ReverseFrame
                {
                    Type = ReverseFrame.Types.StreamData,
                    CorrelationId = this.correlationId,
                    StreamFrameKindByte = (byte)frame.Kind,
                    StreamData = frame.Payload.ToArray(),
                },
                cancellationToken);
        }

        public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await this.inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
