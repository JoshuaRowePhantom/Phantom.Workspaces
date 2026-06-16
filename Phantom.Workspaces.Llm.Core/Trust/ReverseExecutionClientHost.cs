using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// The connecting-instance (C) host that keeps a reverse-execution worker connected to a
/// connected-to instance (S). It opens a duplex channel (a WebSocket in production), runs a
/// <see cref="ReverseExecutionWorker"/> until the connection ends, then reconnects with backoff.
/// Connection transitions are surfaced via <see cref="ConnectionStateChanged"/> so the GUI can show
/// outbound reverse-connection status. See <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public sealed class ReverseExecutionClientHost
{
    private readonly string clientInstanceId;
    private readonly IReverseExecutionHandler handler;
    private readonly Func<CancellationToken, Task<IReverseMessageChannel>> channelFactory;
    private readonly Func<int, CancellationToken, Task> backoffDelay;

    /// <summary>Raised with <see langword="true"/> when connected and <see langword="false"/> when disconnected.</summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>Whether a reverse-execution channel is currently established.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Creates a host with an injectable channel factory (production passes a WebSocket factory; tests
    /// pass an in-memory channel). The optional <paramref name="backoffDelay"/> lets tests make
    /// reconnection deterministic; production uses exponential backoff capped at 30 seconds.
    /// </summary>
    public ReverseExecutionClientHost(
        string clientInstanceId,
        IReverseExecutionHandler handler,
        Func<CancellationToken, Task<IReverseMessageChannel>> channelFactory,
        Func<int, CancellationToken, Task>? backoffDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstanceId);
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        this.clientInstanceId = clientInstanceId;
        this.backoffDelay = backoffDelay ?? DefaultBackoffDelay;
    }

    /// <summary>
    /// Builds a production host that connects to a connected-to instance's <c>/reverse/connect</c>
    /// WebSocket endpoint derived from its base HTTP(S) endpoint.
    /// </summary>
    public static ReverseExecutionClientHost ForEndpoint(
        string baseEndpoint,
        string clientInstanceId,
        IReverseExecutionHandler handler,
        string? devTunnelAccessToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseEndpoint);
        var connectUri = BuildConnectUri(baseEndpoint);

        async Task<IReverseMessageChannel> CreateChannelAsync(CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(devTunnelAccessToken))
            {
                socket.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {devTunnelAccessToken}");
            }

            try
            {
                await socket.ConnectAsync(connectUri, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return new WebSocketReverseMessageChannel(socket);
        }

        return new ReverseExecutionClientHost(clientInstanceId, handler, CreateChannelAsync);
    }

    /// <summary>Connects and runs reverse execution, reconnecting with backoff until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IReverseMessageChannel? channel = null;
            try
            {
                channel = await this.channelFactory(cancellationToken).ConfigureAwait(false);
                attempt = 0;
                this.SetConnected(true);

                var worker = new ReverseExecutionWorker(channel, this.clientInstanceId, this.handler);
                await worker.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Connection or worker failure: fall through to reconnect with backoff.
            }
            finally
            {
                this.SetConnected(false);
                if (channel is not null)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await this.backoffDelay(++attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetConnected(bool connected)
    {
        if (this.IsConnected == connected)
        {
            return;
        }

        this.IsConnected = connected;
        this.ConnectionStateChanged?.Invoke(connected);
    }

    private static Uri BuildConnectUri(string baseEndpoint)
    {
        if (!Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"Reverse host endpoint is not a valid absolute URI: {baseEndpoint}");
        }

        var scheme = baseUri.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            "wss" or "ws" => baseUri.Scheme,
            _ => throw new InvalidOperationException($"Unsupported reverse host endpoint scheme: {baseUri.Scheme}"),
        };

        return new UriBuilder(baseUri) { Scheme = scheme, Path = "/reverse/connect" }.Uri;
    }

    private static async Task DefaultBackoffDelay(int attempt, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)));
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
    }
}
