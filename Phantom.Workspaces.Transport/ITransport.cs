using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// A connected transport that can open message channels and streams.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// Opens a new message channel to the specified endpoint.
    /// </summary>
    /// <param name="request">Connection request descriptor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A connected message channel.</returns>
    Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default);

    /// <summary>
    /// Opens a new raw byte stream to the specified endpoint.
    /// </summary>
    /// <param name="request">Connection request descriptor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A connected stream.</returns>
    Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default);
}
