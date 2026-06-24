using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// An <see cref="IDataAccessLayer"/> that reaches a remote workspace over a dev tunnel located by name
/// and transparently re-establishes the connection when it drops — re-resolving the tunnel (picking up
/// a changed forwarded port) and reconnecting with bounded exponential backoff, without restarting the
/// workspace. Reconnection is driven by the deterministic <see cref="DevTunnelConnectionMonitor"/>; all
/// data operations are retried against the freshly reconnected inner layer.
/// </summary>
public sealed class ReconnectingWebDataAccessLayer : IDataAccessLayer, IDisposable
{
    private readonly DevTunnelConnectionMonitor monitor;
    private readonly Func<Exception, bool> isConnectionFailure;
    private readonly object swapGate = new();
    private readonly object reconnectGate = new();
    private IDataAccessLayer? current;
    private Task? reconnectTask;

    public ReconnectingWebDataAccessLayer(
        Func<CancellationToken, Task<DevTunnelEndpointResolution>> resolveEndpointAsync,
        Func<DevTunnelEndpointResolution, IDataAccessLayer> buildDataAccessLayer,
        IDelayScheduler delayScheduler,
        DevTunnelReconnectOptions? reconnectOptions = null,
        Func<IDataAccessLayer, CancellationToken, Task>? validateConnectionAsync = null,
        Func<Exception, bool>? isConnectionFailure = null,
        Func<double>? nextJitterSample = null)
    {
        ArgumentNullException.ThrowIfNull(resolveEndpointAsync);
        ArgumentNullException.ThrowIfNull(buildDataAccessLayer);
        var validate = validateConnectionAsync ?? DefaultValidateConnectionAsync;
        this.isConnectionFailure = isConnectionFailure ?? DefaultIsConnectionFailure;
        this.monitor = new DevTunnelConnectionMonitor(
            resolveEndpointAsync,
            async (resolution, cancellationToken) =>
            {
                var layer = buildDataAccessLayer(resolution);
                try
                {
                    await validate(layer, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    (layer as IDisposable)?.Dispose();
                    throw;
                }

                this.SwapCurrent(layer);
            },
            delayScheduler,
            reconnectOptions,
            nextJitterSample);
    }

    /// <summary>The current connection status (Connected / Reconnecting / Failed).</summary>
    public DevTunnelConnectionStatus Status => this.monitor.Status;

    /// <summary>Raised whenever the connection status changes.</summary>
    public event EventHandler<DevTunnelConnectionStatus>? StatusChanged
    {
        add => this.monitor.StatusChanged += value;
        remove => this.monitor.StatusChanged -= value;
    }

    /// <summary>Resolves the tunnel and establishes the initial connection. Throws if it cannot connect.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => this.monitor.StartAsync(cancellationToken);

    public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(layer => layer.UpdateAsync(request, cancellationToken), cancellationToken);

    public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(layer => layer.GetAsync(request, cancellationToken), cancellationToken);

    public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(layer => layer.QueryAsync(request, cancellationToken), cancellationToken);

    public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(layer => layer.GetHistoryAsync(request, cancellationToken), cancellationToken);

#pragma warning disable CS0618
    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(layer => layer.ExportAsync(request, cancellationToken), cancellationToken);
#pragma warning restore CS0618

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteAsync(layer => layer.GetChangedEntitiesAsync(request, cancellationToken), cancellationToken);

    public void Dispose()
    {
        lock (this.swapGate)
        {
            (this.current as IDisposable)?.Dispose();
            this.current = null;
        }
    }

    private async Task<TResult> ExecuteAsync<TResult>(Func<IDataAccessLayer, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            var layer = this.current
                ?? throw new InvalidOperationException($"{nameof(ReconnectingWebDataAccessLayer)} must be started before use.");
            try
            {
                return await operation(layer).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && this.isConnectionFailure(exception))
            {
                await this.ReconnectAsync(exception, cancellationToken).ConfigureAwait(false);
                if (this.monitor.Status.State == DevTunnelConnectionState.Failed)
                {
                    throw;
                }

                // Reconnected (new inner layer is in place): retry the operation.
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

    private void SwapCurrent(IDataAccessLayer layer)
    {
        lock (this.swapGate)
        {
            var previous = this.current;
            this.current = layer;
            if (!ReferenceEquals(previous, layer))
            {
                (previous as IDisposable)?.Dispose();
            }
        }
    }

    private static Task DefaultValidateConnectionAsync(IDataAccessLayer layer, CancellationToken cancellationToken)
        => layer.GetAsync(new GetRequest { Entities = [] }, cancellationToken);

    private static bool DefaultIsConnectionFailure(Exception exception)
        => exception is WebDataAccessRequestException { IsConnectivityFailure: true };
}
