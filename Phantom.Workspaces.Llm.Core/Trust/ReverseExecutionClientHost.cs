using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// reconnection deterministic; production uses exponential backoff capped at a 2-minute poll interval.
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

    /// <summary>
    /// Builds a production host that connects to a connected-to instance's <c>/reverse/connect-http</c>
    /// HTTP streaming endpoint derived from its base HTTP(S) endpoint. HTTP/2 is required for
    /// bidirectional streaming; the connection fails (and retries with backoff) if the server does not
    /// support HTTP/2.
    /// </summary>
    /// <param name="baseEndpoint">The base HTTP(S) endpoint of the connected-to instance.</param>
    /// <param name="clientInstanceId">The client's user-computer-profile entity id.</param>
    /// <param name="handler">The local handler that executes agent requests from the server.</param>
    /// <param name="authToken">Optional Bearer token added to outgoing requests (e.g. a dev-tunnel access token).</param>
    /// <param name="httpMessageHandler">Optional <see cref="HttpMessageHandler"/> override; used in tests to route via an in-memory server.</param>
    public static ReverseExecutionClientHost ForEndpointHttp(
        string baseEndpoint,
        string clientInstanceId,
        IReverseExecutionHandler handler,
        string? authToken = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseEndpoint);
        var connectUri = BuildConnectHttpUri(baseEndpoint);

        async Task<IReverseMessageChannel> CreateChannelAsync(CancellationToken cancellationToken)
        {
            var outboundPipe = new Pipe();

            var httpClient = httpMessageHandler is null
                ? new HttpClient()
                : new HttpClient(httpMessageHandler, disposeHandler: false);

            if (authToken is not null)
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, connectUri)
                {
                    Content = new PipeReaderContent(outboundPipe.Reader),
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };

                var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var inboundReader = PipeReader.Create(responseStream);

                return new HttpReverseMessageChannel(
                    inboundReader,
                    outboundPipe.Writer,
                    owned: new OwningGroup(httpClient, response));
            }
            catch
            {
                httpClient.Dispose();
                throw;
            }
        }

        return new ReverseExecutionClientHost(clientInstanceId, handler, CreateChannelAsync);
    }

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

    private static Uri BuildConnectHttpUri(string baseEndpoint)
    {
        if (!Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"Reverse host endpoint is not a valid absolute URI: {baseEndpoint}");
        }

        return new UriBuilder(baseUri) { Path = "/reverse/connect-http" }.Uri;
    }

    private static async Task DefaultBackoffDelay(int attempt, CancellationToken cancellationToken)
    {
        // Exponential backoff capped at a 2-minute poll interval.
        var seconds = Math.Min(120, Math.Pow(2, Math.Min(attempt, 7)));
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An <see cref="HttpContent"/> implementation that streams data from a <see cref="PipeReader"/>
    /// as the HTTP request body. The request body remains open until the pipe is completed.
    /// </summary>
    private sealed class PipeReaderContent : HttpContent
    {
        private readonly PipeReader pipeReader;

        public PipeReaderContent(PipeReader pipeReader) => this.pipeReader = pipeReader;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => this.pipeReader.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => this.pipeReader.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>Groups multiple <see cref="IDisposable"/>s so they are disposed together.</summary>
    private sealed class OwningGroup : IDisposable
    {
        private readonly IDisposable[] owned;

        public OwningGroup(params IDisposable[] owned) => this.owned = owned;

        public void Dispose()
        {
            foreach (var item in this.owned)
            {
                item.Dispose();
            }
        }
    }
}
