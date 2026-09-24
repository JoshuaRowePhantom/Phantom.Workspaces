using System.Text.Json;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport.Logging;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Iterating implementation of ITransportRegistry.
/// </summary>
public sealed class TransportRegistry : ITransportRegistry
{
    private readonly List<ITransportListener> _listeners = [];
    private readonly ILoggerFactory? loggerFactory;
    private readonly ILogger? logger;

    public TransportRegistry(ILoggerFactory? loggerFactory = null)
    {
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<TransportRegistry>();
    }

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
        var marker = this.loggerFactory is null ? null : TransportMetadataTrace.MarkerForRequest(request);
        if (marker is not null)
        {
            this.logger!.LogInformation("Transport worker channel open; attempt {Attempt}; frame {FrameType}; outcome started.",
                marker, TransportMetadataTrace.FrameType(request));
            channel = channel.WithLogging(this.loggerFactory!, marker);
        }
        foreach (var listener in _listeners)
        {
            var result = await listener.OnChannelOpenAsync(request, channel, ct);
            if (result is not null)
            {
                if (marker is not null)
                    this.logger!.LogInformation("Transport worker channel open; attempt {Attempt}; outcome accepted.", marker);
                return result;
            }
        }
        if (marker is not null)
            this.logger!.LogInformation("Transport worker channel open; attempt {Attempt}; outcome no-lease.", marker);
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
        var marker = this.loggerFactory is null ? null : TransportMetadataTrace.MarkerForRequest(request);
        if (marker is not null)
        {
            this.logger!.LogInformation("Transport worker stream open; attempt {Attempt}; frame {FrameType}; outcome started.",
                marker, TransportMetadataTrace.FrameType(request));
            stream = stream.WithLogging(this.loggerFactory!, marker);
        }
        foreach (var listener in _listeners)
        {
            var result = await listener.OnStreamOpenAsync(request, stream, ct);
            if (result is not null)
            {
                if (marker is not null)
                    this.logger!.LogInformation("Transport worker stream open; attempt {Attempt}; outcome accepted.", marker);
                return result;
            }
        }
        if (marker is not null)
            this.logger!.LogInformation("Transport worker stream open; attempt {Attempt}; outcome no-lease.", marker);
        return null;
    }
}
