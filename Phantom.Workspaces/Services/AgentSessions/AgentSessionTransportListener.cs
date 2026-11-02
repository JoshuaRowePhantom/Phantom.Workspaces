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
            || type.GetString() is not ("attach-agent-session" or "take-over-agent-session"
                or "restart-agent-session" or "agent-session-restart-status" or "observe-agent-session-restart"))
            return null;
        lock (this.gate) ObjectDisposedException.ThrowIf(this.disposed, this);
        var stage = "validate-request";
        var attempt = SessionAttachDiagnosticScope.ReadValidated(request);
        string? agentSessionId = null;
        string? ownerProfileEntityId = null;
        try
        {
            var peer = this.peerIdentityProvider.GetRequiredIdentity(channel);
            if (type.GetString() == "observe-agent-session-restart")
            {
                var expected = request.GetProperty("expected-runtime").Deserialize<AgentSessionRuntimeIdentity>(
                    AgentSessionProtocolCodec.Options) ?? throw new RemoteAgentProtocolException("Restart identity was null.");
                var observation = new RestartObservation(this.host, peer, expected, channel, this.RemoveActive);
                lock (this.gate) this.active.Add(observation);
                return observation;
            }
            if (type.GetString() is "restart-agent-session" or "agent-session-restart-status")
            {
                var restart = JsonSerializer.Deserialize<AgentSessionOwnerRestartRequest>(
                    request.GetProperty("request"), AgentSessionProtocolCodec.Options)
                    ?? throw new RemoteAgentProtocolException("Restart request was null.");
                var result = type.GetString() == "agent-session-restart-status"
                    ? await this.host.GetRestartStatusAsync(peer, restart, ct).ConfigureAwait(false)
                    : await this.host.RestartAsync(peer, restart, ct).ConfigureAwait(false);
                await channel.Writer.WriteAsync(
                    JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options), ct).ConfigureAwait(false);
                return null;
            }
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
            attempt ??= open.DiagnosticAttempt;
            agentSessionId = open.AgentSessionId;
            ownerProfileEntityId = open.ExpectedOwningProfileEntityId;
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
            var diagnosticId = Guid.NewGuid();
            this.logger.LogError(error,
                "Remote agent-session attach failed at {Stage} with category {ErrorCategory} and type {ErrorType}. "
                + "attempt {Attempt}; agent-session-id {AgentSessionId}; owner-profile-entity-id {OwnerProfileEntityId}; diagnostic-id {DiagnosticId}.",
                stage,
                category,
                error.GetType().Name,
                attempt ?? "none",
                agentSessionId ?? "unknown",
                ownerProfileEntityId ?? "unknown",
                diagnosticId);
            this.AttachFailed?.Invoke(
                new AgentSessionAttachFailure(stage, category, error));
            await WriteSanitizedErrorAsync(channel, error, diagnosticId, ct).ConfigureAwait(false);
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
        foreach (var lease in leases)
            if (lease is TransportAttachmentHandle attachment)
                await attachment.ShutdownAsync().ConfigureAwait(false);
            else
                await lease.DisposeAsync().ConfigureAwait(false);
        await this.host.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask WriteSanitizedErrorAsync(
        IMessageChannel channel, Exception error, Guid correlationId, CancellationToken ct)
    {
        var (code, operation, message) = error switch
        {
            AgentSessionUnavailableException =>
                ("not-found", "attach", "The agent session is unavailable."),
            AgentSessionTakeoverBlockedException =>
                ("takeover-blocked", "takeover", "The agent session takeover could not be completed."),
            _ => ("invalid-request", "attach", "The attach request could not be processed."),
        };
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

    private sealed class RestartObservation : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly Action<IAsyncDisposable> remove;
        private readonly Task pump;
        private int disposed;

        internal RestartObservation(RemoteAgentSessionHost host, TransportPeerIdentity peer,
            AgentSessionRuntimeIdentity expected, IMessageChannel channel, Action<IAsyncDisposable> remove)
        {
            this.remove = remove;
            this.pump = this.RunAsync(host, peer, expected, channel);
            _ = this.pump.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task RunAsync(RemoteAgentSessionHost host, TransportPeerIdentity peer,
            AgentSessionRuntimeIdentity expected, IMessageChannel channel)
        {
            var writing = WriteAsync();
            var closed = channel.Reader.ReadAsync(this.lifetime.Token).AsTask();
            try
            {
                await Task.WhenAny(writing, closed).ConfigureAwait(false);
                await this.lifetime.CancelAsync().ConfigureAwait(false);
                await writing.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested) { }
            finally
            {
                await ((Task)closed).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            async Task WriteAsync()
            {
                await foreach (var result in host.ObserveRestartAsync(peer, expected, this.lifetime.Token).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(
                        JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options), this.lifetime.Token).ConfigureAwait(false);
                    if (result is null || result.State is AgentSessionOwnerRestartState.Succeeded or AgentSessionOwnerRestartState.Blocked)
                        return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
            this.remove(this);
            await this.lifetime.CancelAsync().ConfigureAwait(false);
            try { await this.pump.ConfigureAwait(false); }
            finally { this.lifetime.Dispose(); }
        }
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
