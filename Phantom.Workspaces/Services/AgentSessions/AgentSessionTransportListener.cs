using System.Text.Json;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Services.AgentSessions;

public sealed class AgentSessionTransportListener : ITransportListener
{
    private readonly RemoteAgentSessionHost host;
    private readonly ITransportPeerIdentityProvider peerIdentityProvider;
    private readonly ILogger<AgentSessionTransportListener> logger;
    private readonly object gate = new();
    private readonly HashSet<IAsyncDisposable> active = [];
    private bool disposed;

    internal AgentSessionTransportListener(
        RemoteAgentSessionHost host,
        ITransportPeerIdentityProvider peerIdentityProvider,
        ILogger<AgentSessionTransportListener>? logger = null)
    {
        this.host = host;
        this.peerIdentityProvider = peerIdentityProvider;
        this.logger = logger ?? NullLogger<AgentSessionTransportListener>.Instance;
    }

    internal event Action<AgentSessionAttachFailure>? AttachFailed;

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(
        JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() is not ("attach-agent-session" or "take-over-agent-session"))
            return null;
        lock (this.gate) ObjectDisposedException.ThrowIf(this.disposed, this);
        var stage = "validate-request";
        try
        {
            var peer = this.peerIdentityProvider.GetRequiredIdentity(channel);
            if (type.GetString() == "take-over-agent-session")
            {
                stage = "deserialize-takeover";
                var takeover = AgentSessionProtocolCodec.DeserializeTakeover(request);
                stage = "takeover";
                await this.host.TakeOverAsync(peer, takeover, ct).ConfigureAwait(false);
                var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    new RuntimeEpoch { Value = Guid.NewGuid() },
                    1,
                    takeover.CorrelationId,
                    new CommandCompletedEvent
                    {
                        CommandId = takeover.CorrelationId,
                        Result = JsonSerializer.SerializeToElement(new { takenOver = true }),
                    });
                await channel.Writer.WriteAsync(
                    AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
                return null;
            }

            stage = "deserialize-open";
            var open = AgentSessionProtocolCodec.DeserializeOpen(request);
            if (open.OpenIntent == AgentSessionOpenIntent.Status)
            {
                stage = "status";
                var status = await this.host.GetStatusAsync(peer, open, ct).ConfigureAwait(false);
                var epoch = open.ReplayCursor?.Epoch ?? new RuntimeEpoch { Value = Guid.NewGuid() };
                var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    epoch, 1, Guid.NewGuid(), new SessionStatusEvent { Status = status });
                await channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
                return null;
            }
            stage = "host-open";
            var attachment = await this.host.OpenAsync(new OpenAgentSessionHostRequest
            {
                Peer = peer,
                OpenRequest = open,
                Channel = channel,
            }, ct).ConfigureAwait(false);
            var handle = new TransportAttachmentHandle(attachment, this.RemoveActive);
            lock (this.gate) this.active.Add(handle);
            return handle;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var category = Categorize(error);
            this.logger.LogError(
                "Remote agent-session attach failed at {Stage} with category {ErrorCategory} and type {ErrorType}.",
                stage,
                category,
                error.GetType().Name);
            this.AttachFailed?.Invoke(
                new AgentSessionAttachFailure(stage, category, error));
            await WriteSanitizedErrorAsync(channel, error, ct).ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            return null;
        }

    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(
        JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public async ValueTask DisposeAsync()
    {
        TransportAttachmentHandle[] leases;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            leases = this.active.OfType<TransportAttachmentHandle>().ToArray();
            this.active.Clear();
        }
        foreach (var lease in leases) await lease.ShutdownAsync().ConfigureAwait(false);
        await this.host.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask WriteSanitizedErrorAsync(
        IMessageChannel channel, Exception error, CancellationToken ct)
    {
        var (code, operation, message) = error switch
        {
            AgentSessionUnavailableException =>
                ("not-found", "attach", "The agent session is unavailable."),
            AgentSessionTakeoverBlockedException =>
                ("takeover-blocked", "takeover", "The agent session takeover could not be completed."),
            _ => ("invalid-request", "attach", "The attach request could not be processed."),
        };
        var correlationId = Guid.NewGuid();
        var completion = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["operation"] = operation,
            ["retryable"] = false,
            ["message"] = message,
            ["correlation-id"] = correlationId,
        });
        var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            new RuntimeEpoch { Value = Guid.NewGuid() }, 1, correlationId, new SessionTerminalEvent
            {
                Reason = code,
                CompletionState = completion,
            });
        await channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
    }

    private static string Categorize(Exception error) => error switch
    {
        AgentSessionUnavailableException => "unavailable",
        AgentSessionTakeoverBlockedException => "takeover-blocked",
        RemoteAgentProtocolException => "invalid-protocol",
        InvalidOperationException => "invalid-operation",
        ArgumentException => "invalid-request",
        _ => "internal-error",
    };

    private void RemoveActive(IAsyncDisposable handle)
    {
        lock (this.gate) this.active.Remove(handle);
    }

    private sealed class TransportAttachmentHandle(
        RemoteAgentAttachmentLease attachment,
        Action<IAsyncDisposable> onDispose) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
            onDispose(this);
            await attachment.MarkTransportLostAsync().ConfigureAwait(false);
        }

        internal async ValueTask ShutdownAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
            await attachment.DisposeAsync().ConfigureAwait(false);
        }
    }

}

internal sealed record AgentSessionAttachFailure(
    string Stage,
    string Category,
    Exception Error);
