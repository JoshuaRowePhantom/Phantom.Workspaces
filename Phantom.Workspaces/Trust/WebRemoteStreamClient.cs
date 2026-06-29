using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// Opens a bidirectional byte stream on a remote Phantom.Workspaces host over a WebSocket connection
/// to <c>GET /stream/open</c>. The <see cref="TrustedStreamRequest"/> is sent as the first JSON text
/// message; all subsequent messages are binary <see cref="StreamFrame"/>s framed with the 5-byte
/// encoding used by <see cref="Phantom.Workspaces.Llm.Shell.StreamFramedMessageChannel"/>.
/// </summary>
public sealed class WebRemoteStreamClient
{
    private readonly string endpoint;
    private readonly string? devTunnelAccessToken;

    public WebRemoteStreamClient(string endpoint, string? devTunnelAccessToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        this.endpoint = endpoint;
        this.devTunnelAccessToken = devTunnelAccessToken;
    }

    /// <summary>
    /// Connects to the remote <c>/stream/open</c> endpoint, negotiates the stream, and returns a
    /// duplex <see cref="Stream"/> that relays data to/from the remote host.
    /// </summary>
    public async Task<Stream> OpenAsync(TrustedStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connectUri = BuildStreamUri(this.endpoint);

        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(this.devTunnelAccessToken))
        {
            socket.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {this.devTunnelAccessToken}");
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

        // Send the stream request as the first JSON text message.
        var requestJson = JsonSerializer.SerializeToUtf8Bytes(request);
        try
        {
            await socket.SendAsync(requestJson, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var channel = new WebSocketStreamMessageChannel(socket, ownsSocket: true);
        return new StreamMessageChannelStream(channel);
    }

    private static Uri BuildStreamUri(string baseEndpoint)
    {
        if (!Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"Remote host endpoint is not a valid absolute URI: {baseEndpoint}");
        }

        var scheme = baseUri.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            "wss" or "ws" => baseUri.Scheme,
            _ => throw new InvalidOperationException($"Unsupported remote host endpoint scheme: {baseUri.Scheme}"),
        };

        return new UriBuilder(baseUri) { Scheme = scheme, Path = "/stream/open" }.Uri;
    }
}
