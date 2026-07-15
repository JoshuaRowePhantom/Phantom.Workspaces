using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Iterating implementation of ITransportRegistry.
/// </summary>
public sealed class TransportRegistry : ITransportRegistry
{
    private readonly List<ITransportListener> _listeners = [];

    /// <inheritdoc />
    public void Register(ITransportListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listeners.Add(listener);
    }

    /// <summary>
    /// Attempts to handle a channel open request by dispatching to registered listeners.
    /// </summary>
    /// <param name="request">Request descriptor.</param>
    /// <param name="channel">The opened channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable resource if a listener handles the request; null otherwise.</returns>
    public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        foreach (var listener in _listeners)
        {
            var result = await listener.OnChannelOpenAsync(request, channel, ct);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// Attempts to handle a stream open request by dispatching to registered listeners.
    /// </summary>
    /// <param name="request">Request descriptor.</param>
    /// <param name="stream">The opened stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable resource if a listener handles the request; null otherwise.</returns>
    public async Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
    {
        foreach (var listener in _listeners)
        {
            var result = await listener.OnStreamOpenAsync(request, stream, ct);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }
}
