using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.ReverseHttp;

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
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> hubLoops = [];
    private readonly SemaphoreSlim startGate = new(1, 1);
    private bool started;
    private bool disposed;

    public WorkspacesTransportHost(
        TransportRegistry localListeners,
        IReadOnlyList<ReverseHttpClientTransportFactory> hubFactories)
    {
        this.localListeners = localListeners ?? throw new ArgumentNullException(nameof(localListeners));
        this.hubFactories = hubFactories ?? throw new ArgumentNullException(nameof(hubFactories));
    }

    public event EventHandler? ConnectionStateChanged;

    public IReadOnlyList<ReverseHttpClientTransportFactory> HubFactories => this.hubFactories;

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

            foreach (var factory in this.hubFactories)
            {
                var channel = await factory.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
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
            var dispatcher = new ReverseExecutionDispatcher(current, this.localListeners);
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

            await dispatcher.DisposeAsync().ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                current = await factory.ReconnectAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Unable to re-establish the registration channel; stop servicing this hub.
                this.OnConnectionStateChanged();
                return;
            }

            this.OnConnectionStateChanged();
        }
    }

    private void OnConnectionStateChanged()
        => this.ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
}
