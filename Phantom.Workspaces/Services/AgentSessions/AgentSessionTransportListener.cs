using System.Text.Json;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services.AgentSessions;

public sealed class AgentSessionTransportListener : ITransportListener
{
    private readonly RemoteAgentSessionHost host;
    private readonly ITransportPeerIdentityProvider peerIdentityProvider;
    private readonly object gate = new();
    private readonly HashSet<IAsyncDisposable> active = [];
    private bool disposed;

    internal AgentSessionTransportListener(
        RemoteAgentSessionHost host, ITransportPeerIdentityProvider peerIdentityProvider)
    {
        this.host = host;
        this.peerIdentityProvider = peerIdentityProvider;
    }

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(
        JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("type", out var type)
            || type.GetString() != "attach-agent-session")
            return null;
        lock (this.gate) ObjectDisposedException.ThrowIf(this.disposed, this);
        try
        {
            var open = AgentSessionProtocolCodec.DeserializeOpen(request);
            var peer = this.peerIdentityProvider.GetRequiredIdentity(channel);
            if (open.OpenIntent == AgentSessionOpenIntent.Status)
            {
                var status = await this.host.GetStatusAsync(peer, open, ct).ConfigureAwait(false);
                var epoch = open.ReplayCursor?.Epoch ?? new RuntimeEpoch { Value = Guid.NewGuid() };
                var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    epoch, 1, Guid.NewGuid(), new SessionStatusEvent { Status = status });
                await channel.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
                return null;
            }
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
        IAsyncDisposable[] leases;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            leases = this.active.ToArray();
            this.active.Clear();
        }
        foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
        await this.host.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask WriteSanitizedErrorAsync(
        IMessageChannel channel, Exception error, CancellationToken ct)
    {
        var code = error is AgentSessionUnavailableException ? "not-found" : "invalid-request";
        var message = error is AgentSessionUnavailableException
            ? "The agent session is unavailable."
            : "The attach request could not be processed.";
        var correlationId = Guid.NewGuid();
        var completion = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["operation"] = "attach",
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
            await attachment.DisposeAsync().ConfigureAwait(false);
        }
    }
}
