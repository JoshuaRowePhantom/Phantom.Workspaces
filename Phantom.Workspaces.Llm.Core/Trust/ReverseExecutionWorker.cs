using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Executes a reverse-execution request locally on the connecting instance (C), streaming the
/// resulting <see cref="ChatResponseUpdate"/>s. The production implementation runs the agent through
/// the normal local trusted-execution path so C enforces its own trust profile; tests supply a stub.
/// </summary>
public interface IReverseExecutionHandler
{
    IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(RemoteAgentRequest request, CancellationToken cancellationToken);
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
}
