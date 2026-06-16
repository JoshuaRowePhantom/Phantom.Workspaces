using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

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
    private readonly CancellationTokenSource closed = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? readLoop;

    public ReverseChannelConnection(IReverseMessageChannel channel, string clientInstanceId, DateTimeOffset connectedAt)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        this.ClientInstanceId = clientInstanceId;
        this.ConnectedAt = connectedAt;
    }

    public string ClientInstanceId { get; }

    public DateTimeOffset ConnectedAt { get; }

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
}
