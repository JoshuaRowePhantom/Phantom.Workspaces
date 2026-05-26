using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Phantom.Workspaces.Llm;

public sealed class DelegatingMcpServer : IClientTransport, IAsyncDisposable
{
    private readonly IClientTransport delegatedClientTransport;
    private readonly McpClientOptions? delegatedClientOptions;
    private readonly object syncLock = new();
    private ITransport? activeDelegatedTransport;

    public DelegatingMcpServer(
        IClientTransport delegatedClientTransport,
        McpClientOptions? delegatedClientOptions = null)
    {
        this.delegatedClientTransport = delegatedClientTransport
                                        ?? throw new ArgumentNullException(nameof(delegatedClientTransport));
        this.delegatedClientOptions = delegatedClientOptions;
    }

    public string Name => $"Delegating ({this.delegatedClientTransport.Name})";

    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
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
            await delegatedTransport.DisposeAsync();
            lock (this.syncLock)
            {
                if (ReferenceEquals(this.activeDelegatedTransport, delegatedTransport))
                {
                    this.activeDelegatedTransport = null;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        ITransport? delegatedTransport;
        lock (this.syncLock)
        {
            delegatedTransport = this.activeDelegatedTransport;
            this.activeDelegatedTransport = null;
        }

        if (delegatedTransport is not null)
        {
            await delegatedTransport.DisposeAsync();
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
