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
public sealed class ReconnectingWebDataAccessLayer : IDataAccessLayer, IDisposable, IAsyncDisposable
{
    private readonly DevTunnelConnectionMonitor monitor;
    private readonly Func<Exception, bool> isConnectionFailure;
    private readonly object swapGate = new();
    private readonly object reconnectGate = new();
    private IDataAccessLayer? current;
    private Task? reconnectTask;
    private Task? startTask;
    private Task? disposeTask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource synchronousDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

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
                IDataAccessLayer? previous;
                try
                {
                    await validate(layer, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    previous = this.SwapCurrent(layer, cancellationToken);
                }
                catch
                {
                    (layer as IDisposable)?.Dispose();
                    throw;
                }
                (previous as IDisposable)?.Dispose();
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
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (this.reconnectGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
            return this.startTask ??= this.StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        await this.monitor.StartAsync(linked.Token).ConfigureAwait(false);
    }

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
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }
        try
        {
            try
            {
                this.lifetime.Cancel();
            }
            finally
            {
                IDataAccessLayer? previous;
                lock (this.swapGate)
                {
                    previous = this.current;
                    this.current = null;
                }
                (previous as IDisposable)?.Dispose();
            }
            this.synchronousDisposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.synchronousDisposal.TrySetException(exception);
            _ = this.synchronousDisposal.Task.Exception;
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.reconnectGate)
        {
            return new ValueTask(this.disposeTask ??= this.DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? cancellationFailure = null;
        try
        {
            this.Dispose();
            await this.synchronousDisposal.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
        }
        Task[] pending;
        lock (this.reconnectGate)
        {
            pending = new[] { this.startTask, this.reconnectTask }.OfType<Task>().ToArray();
        }
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this.lifetime.Dispose();
        }
        if (cancellationFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
        }
    }

    private async Task<TResult> ExecuteAsync<TResult>(Func<IDataAccessLayer, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
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
                ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
                this.reconnectTask = this.ReconnectCoreAsync(failure, cancellationToken);
            }

            return this.reconnectTask;
        }
    }

    private async Task ReconnectCoreAsync(Exception failure, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        await this.monitor.HandleConnectionFailedAsync(failure, linked.Token).ConfigureAwait(false);
    }

    private IDataAccessLayer? SwapCurrent(IDataAccessLayer layer, CancellationToken cancellationToken)
    {
        lock (this.swapGate)
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            var previous = this.current;
            this.current = layer;
            return ReferenceEquals(previous, layer) ? null : previous;
        }
    }

    private static Task DefaultValidateConnectionAsync(IDataAccessLayer layer, CancellationToken cancellationToken)
        => layer.GetAsync(new GetRequest { Entities = [] }, cancellationToken);

    private static bool DefaultIsConnectionFailure(Exception exception)
        => exception is WebDataAccessRequestException { IsConnectivityFailure: true };
}
