using Phantom.Workspaces.Data;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Transport.Logging;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services;

/// <summary>
/// GUI-side transport host that registers this machine with each configured reverse-HTTP hub and
/// services the returned registration channel with a <see cref="ReverseExecutionDispatcher"/>, so
/// relayed <c>channel-open</c> / <c>stream-open</c> frames reach the local
/// <see cref="TransportRegistry"/> of chat/mcp/shell listeners. On loss of a registration channel it
/// reconnects via <see cref="ReverseHttpClientTransportFactory.ReconnectAsync"/> and re-hosts the
/// dispatcher on the fresh channel. Replaces the <c>ReverseExecutionClientHost</c> /
/// <c>ReverseConnectionAcceptor</c> / <c>LocalReverseExecutionHandler</c> role.
/// </summary>
public sealed class WorkspacesTransportHost : IAsyncDisposable
{
    private readonly TransportRegistry localListeners;
    private readonly IReadOnlyList<ReverseHttpClientTransportFactory> hubFactories;
    private readonly TransportPeerIdentityProvider? peerIdentities;
    private readonly IReachabilityRouteStore? reachabilityRouteStore;
    private readonly EntityId? localProfileEntityId;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly TransportMetadataLoggingOptions metadataLogging;
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> hubLoops = [];
    private readonly SemaphoreSlim startGate = new(1, 1);
    private bool started;
    private bool disposed;

    public WorkspacesTransportHost(
        TransportRegistry localListeners,
        IReadOnlyList<ReverseHttpClientTransportFactory> hubFactories,
        ILoggerFactory loggerFactory,
        TransportPeerIdentityProvider? peerIdentities = null,
        IReachabilityRouteStore? reachabilityRouteStore = null,
        EntityId? localProfileEntityId = null,
        TransportMetadataLoggingOptions? metadataLogging = null)
    {
        this.localListeners = localListeners ?? throw new ArgumentNullException(nameof(localListeners));
        this.hubFactories = hubFactories ?? throw new ArgumentNullException(nameof(hubFactories));
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<WorkspacesTransportHost>();
        this.metadataLogging = metadataLogging ?? TransportMetadataLoggingOptions.FromEnvironment();
        this.peerIdentities = peerIdentities;
        this.reachabilityRouteStore = reachabilityRouteStore;
        this.localProfileEntityId = localProfileEntityId;
    }

    public event EventHandler? ConnectionStateChanged;

    public IReadOnlyList<ReverseHttpClientTransportFactory> HubFactories => this.hubFactories;

    public Exception? LastReachabilityPublicationError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.started)
            {
                return;
            }

            if (this.reachabilityRouteStore is not null && this.localProfileEntityId is not null)
            {
                try
                {
                    await this.reachabilityRouteStore.ClearOwnedRoutesAsync(
                        this.localProfileEntityId.Value,
                        this.localProfileEntityId.Value,
                        cancellationToken).ConfigureAwait(false);
                    this.LastReachabilityPublicationError = null;
                }
                catch (ReachabilityRouteStoreException exception)
                {
                    this.LastReachabilityPublicationError = exception;
                }
            }

            foreach (var factory in this.hubFactories)
            {
                var channel = await factory.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
                this.logger.LogInformation("Reverse worker registration; outcome connected.");
                this.hubLoops.Add(this.RunHubAsync(factory, channel));
                this.OnConnectionStateChanged();
            }

            this.started = true;
        }
        finally
        {
            this.startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        await this.shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(this.hubLoops).ConfigureAwait(false);
        }
        catch
        {
        }

        foreach (var factory in this.hubFactories)
        {
            await factory.DisposeAsync().ConfigureAwait(false);
        }

        this.startGate.Dispose();
        this.shutdown.Dispose();
    }

    private async Task RunHubAsync(ReverseHttpClientTransportFactory factory, IMessageChannel channel)
    {
        var token = this.shutdown.Token;
        var current = channel;

        while (!token.IsCancellationRequested)
        {
            var dispatcher = new ReverseExecutionDispatcher(
                current,
                this.localListeners,
                this.peerIdentities,
                factory.ApplyHubProfileEntityIdAsync,
                this.loggerFactory,
                this.metadataLogging);
            try
            {
                await current.Reader.Completion.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await dispatcher.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                // The registration channel faulted; fall through to reconnect below.
            }

            this.logger.LogInformation("Reverse worker registration; outcome disconnected.");
            await dispatcher.DisposeAsync().ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                current = await factory.ReconnectAsync(token).ConfigureAwait(false);
                this.logger.LogInformation("Reverse worker registration; outcome reconnected.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Unable to re-establish the registration channel; stop servicing this hub.
                this.logger.LogWarning("Reverse worker registration; outcome reconnect-failed.");
                this.OnConnectionStateChanged();
                return;
            }

            this.OnConnectionStateChanged();
        }
    }

    private void OnConnectionStateChanged()
        => this.ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
}
