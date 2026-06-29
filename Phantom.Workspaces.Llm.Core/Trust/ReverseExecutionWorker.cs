using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Executes a reverse-execution request locally on the connecting instance (C), streaming the
/// resulting <see cref="ChatResponseUpdate"/>s. The production implementation runs the agent through
/// the normal local trusted-execution path so C enforces its own trust profile; tests supply a stub.
/// </summary>
public interface IReverseExecutionHandler
{
    IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(RemoteAgentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a local stream for the given <paramref name="streamKind"/> and relays it over the
    /// supplied <paramref name="channel"/> until the stream ends or <paramref name="cancellationToken"/>
    /// fires. Implementations run the handler (e.g. shell) directly on C and pump frames through the
    /// channel so the server side sees the bidirectional stream.
    /// </summary>
    Task HandleStreamAsync(
        string streamKind,
        string openPayloadJson,
        IStreamMessageChannel channel,
        CancellationToken cancellationToken);

    /// <summary>Executes a workspace tool locally on behalf of a server-pushed run-tool request.</summary>
    Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The connecting-instance (C) worker for reverse execution. It registers over the duplex channel,
/// then handles each <c>execute</c> frame pushed by the server by running the agent locally and
/// streaming <c>update</c>/<c>complete</c> frames back. See
/// <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public sealed class ReverseExecutionWorker
{
    private readonly IReverseMessageChannel channel;
    private readonly string clientInstanceId;
    private readonly IReverseExecutionHandler handler;
    private readonly ConcurrentDictionary<string, WorkerStreamChannel> activeStreams = new(StringComparer.Ordinal);

    public ReverseExecutionWorker(
        IReverseMessageChannel channel,
        string clientInstanceId,
        IReverseExecutionHandler handler)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.clientInstanceId = clientInstanceId;
    }

    /// <summary>Registers and processes reverse-execution requests until the channel closes or is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await this.channel.SendAsync(
            new ReverseFrame { Type = ReverseFrame.Types.Register, ClientInstanceId = this.clientInstanceId },
            cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await this.channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }

            if (frame.Type == ReverseFrame.Types.Execute
                && frame.CorrelationId is { } correlationId
                && frame.Request is { } request)
            {
                // Each turn runs concurrently; the channel serializes its own sends.
                _ = this.HandleExecuteAsync(correlationId, request, cancellationToken);
            }
            else if (frame.Type == ReverseFrame.Types.OpenStream
                && frame.CorrelationId is { } streamId
                && frame.StreamKind is { } streamKind)
            {
                _ = this.HandleOpenStreamAsync(streamId, streamKind, frame.StreamOpenPayload ?? "{}", cancellationToken);
            }
            else if (frame.Type == ReverseFrame.Types.RunTool
                && frame.CorrelationId is { } runToolId
                && frame.ToolRequest is { } toolRequest)
            {
                _ = this.HandleRunToolAsync(runToolId, toolRequest, cancellationToken);
            }
            else if (frame.Type == ReverseFrame.Types.StreamData
                && frame.CorrelationId is { } dataId
                && this.activeStreams.TryGetValue(dataId, out var dataStream))
            {
                dataStream.Deliver(new StreamFrame(
                    (StreamFrameKind)(frame.StreamFrameKindByte ?? 0),
                    frame.StreamData ?? Array.Empty<byte>()));
            }
            else if (frame.Type == ReverseFrame.Types.StreamClose
                && frame.CorrelationId is { } closeId
                && this.activeStreams.TryRemove(closeId, out var closedStream))
            {
                closedStream.CompleteInbound();
            }
        }
    }

    private async Task HandleExecuteAsync(string correlationId, RemoteAgentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in this.handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await this.channel.SendAsync(
                    new ReverseFrame { Type = ReverseFrame.Types.Update, CorrelationId = correlationId, Update = update },
                    cancellationToken).ConfigureAwait(false);
            }

            await this.channel.SendAsync(
                new ReverseFrame { Type = ReverseFrame.Types.Complete, CorrelationId = correlationId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await this.channel.SendAsync(
                new ReverseFrame
                {
                    Type = ReverseFrame.Types.Complete,
                    CorrelationId = correlationId,
                    Error = new ReverseExecutionError("execution-failed", exception.Message),
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleOpenStreamAsync(
        string correlationId,
        string streamKind,
        string openPayloadJson,
        CancellationToken cancellationToken)
    {
        var workerChannel = new WorkerStreamChannel(this.channel, correlationId);
        if (!this.activeStreams.TryAdd(correlationId, workerChannel))
        {
            return;
        }

        try
        {
            await this.handler.HandleStreamAsync(streamKind, openPayloadJson, workerChannel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
        }
        finally
        {
            this.activeStreams.TryRemove(correlationId, out _);
            await workerChannel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleRunToolAsync(
        string correlationId,
        TrustedToolRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.handler.RunToolAsync(request, cancellationToken).ConfigureAwait(false);

            await this.channel.SendAsync(
                new ReverseFrame { Type = ReverseFrame.Types.RunToolComplete, CorrelationId = correlationId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await this.channel.SendAsync(
                new ReverseFrame
                {
                    Type = ReverseFrame.Types.RunToolComplete,
                    CorrelationId = correlationId,
                    Error = new ReverseExecutionError("execution-failed", exception.Message),
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An <see cref="IStreamMessageChannel"/> that relays <see cref="StreamFrame"/>s from the local
    /// handler (e.g. shell) back to the server as <c>stream-data</c> ReverseFrames. The worker
    /// delivers inbound frames (S→C user input) via <see cref="Deliver"/> and signals stream
    /// closure via <see cref="CompleteInbound"/>; on dispose it sends a <c>stream-close</c> to S.
    /// </summary>
    private sealed class WorkerStreamChannel : IStreamMessageChannel
    {
        private readonly IReverseMessageChannel reverseChannel;
        private readonly string correlationId;
        private readonly Channel<StreamFrame> inbound =
            Channel.CreateUnbounded<StreamFrame>(new UnboundedChannelOptions { SingleReader = true });

        public WorkerStreamChannel(IReverseMessageChannel reverseChannel, string correlationId)
        {
            this.reverseChannel = reverseChannel;
            this.correlationId = correlationId;
        }

        public void Deliver(StreamFrame frame) => this.inbound.Writer.TryWrite(frame);

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

        public async ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            try
            {
                await this.reverseChannel.SendAsync(
                    new ReverseFrame
                    {
                        Type = ReverseFrame.Types.StreamClose,
                        CorrelationId = this.correlationId,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Channel may already be closed; ignore.
            }
        }
    }
}
