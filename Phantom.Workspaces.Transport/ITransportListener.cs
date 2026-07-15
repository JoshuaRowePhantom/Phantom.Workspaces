using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Server-side listener that handles incoming channel and stream requests.
/// </summary>
public interface ITransportListener : IAsyncDisposable
{
    /// <summary>
    /// Handles an incoming channel open request.
    /// </summary>
    /// <param name="request">Request descriptor.</param>
    /// <param name="channel">The opened channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable resource if this listener handles the request; null otherwise.</returns>
    Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default);

    /// <summary>
    /// Handles an incoming stream open request.
    /// </summary>
    /// <param name="request">Request descriptor.</param>
    /// <param name="stream">The opened stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable resource if this listener handles the request; null otherwise.</returns>
    Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default);
}
