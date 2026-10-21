using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm.Interfaces;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Services.DevTunnel;

public sealed class ReconnectingWebAgentPersistenceStore : IAgentPersistenceStore, IDisposable, IAsyncDisposable
{
    private readonly DevTunnelConnectionMonitor monitor;
    private readonly Func<Exception, bool> isConnectionFailure;
    private readonly object swapGate = new();
    private readonly object reconnectGate = new();
    private IAgentPersistenceStore? current;
    private Task? reconnectTask;
    private Task? startTask;
    private Task? disposeTask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource synchronousDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

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
                IAgentPersistenceStore? previous;
                try
                {
                    await validate(store, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    previous = this.SwapCurrent(store, cancellationToken);
                }
                catch
                {
                    (store as IDisposable)?.Dispose();
                    throw;
                }
                (previous as IDisposable)?.Dispose();
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
                IAgentPersistenceStore? previous;
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

    private async ValueTask<TResult> ExecuteAsync<TResult>(Func<IAgentPersistenceStore, ValueTask<TResult>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
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
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
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

    private IAgentPersistenceStore? SwapCurrent(IAgentPersistenceStore store, CancellationToken cancellationToken)
    {
        lock (this.swapGate)
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            var previous = this.current;
            this.current = store;
            return ReferenceEquals(previous, store) ? null : previous;
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
