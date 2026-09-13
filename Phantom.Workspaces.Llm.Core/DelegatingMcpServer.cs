using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Phantom.Workspaces.Llm;

public sealed class DelegatingMcpServer : IClientTransport, IAsyncDisposable
{
    private readonly IClientTransport delegatedClientTransport;
    private readonly McpClientOptions? delegatedClientOptions;
    private ITransport? preconnectedTransport;
    private readonly object syncLock = new();
    private ITransport? activeDelegatedTransport;
    private Task? activeTransportDisposal;
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

        var delegatedTransport = await this.ConnectAsync(cancellationToken);
        lock (this.syncLock)
        {
            this.activeDelegatedTransport = delegatedTransport;
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var forwardIncoming = ForwardAsync(
                incomingServerTransport.MessageReader,
                delegatedTransport,
                linkedCts.Token);
            var forwardDelegated = ForwardAsync(
                delegatedTransport.MessageReader,
                incomingServerTransport,
                linkedCts.Token);

            var completedTask = await Task.WhenAny(forwardIncoming, forwardDelegated);
            if (completedTask.IsFaulted)
            {
                await completedTask;
            }

            linkedCts.Cancel();
            await Task.WhenAll(
                SuppressCancellation(forwardIncoming),
                SuppressCancellation(forwardDelegated));
        }
        finally
        {
            await this.DisposeActiveTransportAsync(delegatedTransport);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        ITransport? delegatedTransport;
        lock (this.syncLock)
        {
            delegatedTransport = this.activeDelegatedTransport;
        }

        if (delegatedTransport is not null)
        {
            await this.DisposeActiveTransportAsync(delegatedTransport);
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

    private Task DisposeActiveTransportAsync(ITransport transport)
    {
        lock (this.syncLock)
        {
            if (this.activeTransportDisposal is not null)
            {
                return this.activeTransportDisposal;
            }

            if (ReferenceEquals(this.activeDelegatedTransport, transport))
            {
                this.activeDelegatedTransport = null;
            }
            this.activeTransportDisposal = transport.DisposeAsync().AsTask();
            return this.activeTransportDisposal;
        }
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

    private static async Task SuppressCancellation(
        Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

}
