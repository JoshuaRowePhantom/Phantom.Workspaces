using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Phantom.Workspaces.Llm;

public sealed class DelegatingMcpServer : IClientTransport, IAsyncDisposable
{
    private readonly IClientTransport delegatedClientTransport;
    private readonly McpClientOptions? delegatedClientOptions;
    private ITransport? preconnectedTransport;
    private readonly object syncLock = new();
    private readonly SemaphoreSlim runLock = new(1, 1);
    private ActiveRun? activeRun;
    private int disposed;

    public DelegatingMcpServer(
        IClientTransport delegatedClientTransport,
        McpClientOptions? delegatedClientOptions = null)
    {
        this.delegatedClientTransport = delegatedClientTransport
                                        ?? throw new ArgumentNullException(nameof(delegatedClientTransport));
        this.delegatedClientOptions = delegatedClientOptions;
    }

    public DelegatingMcpServer(
        IClientTransport delegatedClientTransport,
        ITransport preconnectedTransport)
        : this(delegatedClientTransport)
    {
        this.preconnectedTransport = preconnectedTransport
                                     ?? throw new ArgumentNullException(nameof(preconnectedTransport));
    }

    public string Name => $"Delegating ({this.delegatedClientTransport.Name})";

    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        if (Interlocked.Exchange(ref this.preconnectedTransport, null) is { } preconnected)
        {
            return Task.FromResult(preconnected);
        }

        return this.delegatedClientTransport.ConnectAsync(cancellationToken);
    }

    public async Task RunAsync(
        ITransport incomingServerTransport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingServerTransport);
        await this.runLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        ActiveRun? run = null;
        Exception? relayFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var delegatedTransport = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
            run = new ActiveRun(delegatedTransport, cancellationToken);
            lock (this.syncLock)
            {
                this.activeRun = run;
            }

            var forwardIncoming = ForwardAsync(
                incomingServerTransport.MessageReader,
                delegatedTransport,
                run.Cancellation.Token);
            var forwardDelegated = ForwardAsync(
                delegatedTransport.MessageReader,
                incomingServerTransport,
                run.Cancellation.Token);

            var completedTask = await Task.WhenAny(forwardIncoming, forwardDelegated).ConfigureAwait(false);
            var cancellationFailure = await CaptureFailureAsync(
                run.Cancellation.CancelAsync()).ConfigureAwait(false);
            var incomingFailure = await CaptureFailureAsync(forwardIncoming).ConfigureAwait(false);
            var delegatedFailure = await CaptureFailureAsync(forwardDelegated).ConfigureAwait(false);

            var primaryFailure = ReferenceEquals(completedTask, forwardIncoming)
                ? incomingFailure
                : delegatedFailure;
            var secondaryFailure = ReferenceEquals(completedTask, forwardIncoming)
                ? delegatedFailure
                : incomingFailure;
            relayFailure = primaryFailure ?? secondaryFailure ?? cancellationFailure;
        }
        finally
        {
            if (run is not null)
            {
                cleanupFailure = await CaptureFailureAsync(
                    run.DisposeTransportAsync()).ConfigureAwait(false);
                run.Cancellation.Dispose();
                lock (this.syncLock)
                {
                    if (ReferenceEquals(this.activeRun, run))
                    {
                        this.activeRun = null;
                    }
                }
            }

            this.runLock.Release();
        }

        var failure = relayFailure ?? cleanupFailure;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        ActiveRun? run;
        lock (this.syncLock)
        {
            run = this.activeRun;
        }

        if (run is not null)
        {
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
            await run.DisposeTransportAsync().ConfigureAwait(false);
        }
        else if (Interlocked.Exchange(ref this.preconnectedTransport, null) is { } preconnected)
        {
            await preconnected.DisposeAsync();
        }

        if (this.delegatedClientTransport is IAsyncDisposable owner)
        {
            await owner.DisposeAsync();
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
        if (task.IsFaulted)
        {
            return task.Exception?.InnerException
                   ?? new IOException("MCP relay failed.");
        }

        return null;
    }

    private static async Task ForwardAsync(
        ChannelReader<JsonRpcMessage> reader,
        ITransport destination,
        CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            await destination.SendMessageAsync(message, cancellationToken);
        }
    }

    private sealed class ActiveRun(ITransport transport, CancellationToken cancellationToken)
    {
        private readonly object gate = new();
        private Task? transportDisposal;

        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        public Task DisposeTransportAsync()
        {
            lock (this.gate)
            {
                return this.transportDisposal ??= DisposeTransportAsync(transport);
            }
        }

        private static async Task DisposeTransportAsync(ITransport transport) =>
            await transport.DisposeAsync().ConfigureAwait(false);
    }
}
