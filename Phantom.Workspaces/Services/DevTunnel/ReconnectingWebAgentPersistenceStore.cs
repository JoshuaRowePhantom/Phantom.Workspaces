using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm.Interfaces;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Services.DevTunnel;

public sealed class ReconnectingWebAgentPersistenceStore : IAgentPersistenceStore, IDisposable
{
    private readonly DevTunnelConnectionMonitor monitor;
    private readonly Func<Exception, bool> isConnectionFailure;
    private readonly object swapGate = new();
    private readonly object reconnectGate = new();
    private IAgentPersistenceStore? current;
    private Task? reconnectTask;

    public ReconnectingWebAgentPersistenceStore(
        Func<CancellationToken, Task<DevTunnelEndpointResolution>> resolveEndpointAsync,
        Func<DevTunnelEndpointResolution, IAgentPersistenceStore> buildAgentPersistenceStore,
        IDelayScheduler delayScheduler,
        DevTunnelReconnectOptions? reconnectOptions = null,
        Func<IAgentPersistenceStore, CancellationToken, Task>? validateConnectionAsync = null,
        Func<Exception, bool>? isConnectionFailure = null,
        Func<double>? nextJitterSample = null)
    {
        ArgumentNullException.ThrowIfNull(resolveEndpointAsync);
        ArgumentNullException.ThrowIfNull(buildAgentPersistenceStore);
        var validate = validateConnectionAsync ?? DefaultValidateConnectionAsync;
        this.isConnectionFailure = isConnectionFailure ?? DefaultIsConnectionFailure;
        this.monitor = new DevTunnelConnectionMonitor(
            resolveEndpointAsync,
            async (resolution, cancellationToken) =>
            {
                var store = buildAgentPersistenceStore(resolution);
                try
                {
                    await validate(store, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    (store as IDisposable)?.Dispose();
                    throw;
                }

                this.SwapCurrent(store);
            },
            delayScheduler,
            reconnectOptions,
            nextJitterSample);
    }

    public DevTunnelConnectionStatus Status => this.monitor.Status;

    public event EventHandler<DevTunnelConnectionStatus>? StatusChanged
    {
        add => this.monitor.StatusChanged += value;
        remove => this.monitor.StatusChanged -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => this.monitor.StartAsync(cancellationToken);

    public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(store => store.StoreAsync(request, cancellationToken), cancellationToken);

    public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(store => store.RestoreAsync(request, cancellationToken), cancellationToken);

    public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(store => store.ReadMessagesAsync(request, cancellationToken), cancellationToken);

    public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(store => store.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(store => store.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken), cancellationToken);

    public void Dispose()
    {
        lock (this.swapGate)
        {
            (this.current as IDisposable)?.Dispose();
            this.current = null;
        }
    }

    private async ValueTask<TResult> ExecuteAsync<TResult>(Func<IAgentPersistenceStore, ValueTask<TResult>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            var store = this.current
                ?? throw new InvalidOperationException($"{nameof(ReconnectingWebAgentPersistenceStore)} must be started before use.");
            try
            {
                return await operation(store).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && this.isConnectionFailure(exception))
            {
                await this.ReconnectAsync(exception, cancellationToken).ConfigureAwait(false);
                if (this.monitor.Status.State == DevTunnelConnectionState.Failed)
                {
                    throw;
                }
            }
        }
    }

    private async ValueTask ExecuteAsync(Func<IAgentPersistenceStore, ValueTask> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            var store = this.current
                ?? throw new InvalidOperationException($"{nameof(ReconnectingWebAgentPersistenceStore)} must be started before use.");
            try
            {
                await operation(store).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && this.isConnectionFailure(exception))
            {
                await this.ReconnectAsync(exception, cancellationToken).ConfigureAwait(false);
                if (this.monitor.Status.State == DevTunnelConnectionState.Failed)
                {
                    throw;
                }
            }
        }
    }

    private Task ReconnectAsync(Exception failure, CancellationToken cancellationToken)
    {
        lock (this.reconnectGate)
        {
            if (this.reconnectTask is null || this.reconnectTask.IsCompleted)
            {
                this.reconnectTask = this.monitor.HandleConnectionFailedAsync(failure, cancellationToken);
            }

            return this.reconnectTask;
        }
    }

    private void SwapCurrent(IAgentPersistenceStore store)
    {
        lock (this.swapGate)
        {
            var previous = this.current;
            this.current = store;
            if (!ReferenceEquals(previous, store))
            {
                (previous as IDisposable)?.Dispose();
            }
        }
    }

    private static async Task DefaultValidateConnectionAsync(IAgentPersistenceStore store, CancellationToken cancellationToken)
    {
        try
        {
            await store.RestoreAsync(new RestoreRequest { AgentSessionId = "connection-test" }, cancellationToken).ConfigureAwait(false);
        }
        catch (WebDataAccessRequestException)
        {
        }
    }

    private static bool DefaultIsConnectionFailure(Exception exception)
        => exception is WebDataAccessRequestException { IsConnectivityFailure: true };
}
