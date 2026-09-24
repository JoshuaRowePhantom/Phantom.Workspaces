using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Transport.Logging;

/// <summary>
/// Decorator for <see cref="ITransportListener"/> that logs channel/stream open (accept), close, and
/// error events, and auto-wraps the opened channel so its send/receive events are logged too. The
/// inner listener's behavior is otherwise unchanged.
/// </summary>
internal sealed class LoggingTransportListener : ITransportListener
{
    private readonly ITransportListener inner;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;

    public LoggingTransportListener(ITransportListener inner, ILoggerFactory loggerFactory)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.logger = loggerFactory.CreateLogger<LoggingTransportListener>();
    }

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(
        JsonElement request,
        IMessageChannel channel,
        CancellationToken ct = default)
    {
        var marker = TransportMetadataTrace.MarkerForRequest(request);
        this.logger.LogInformation("Transport channel open; attempt {Attempt}; frame {FrameType}; outcome started.",
            marker, TransportMetadataTrace.FrameType(request));
        try
        {
            var session = await this.inner
                .OnChannelOpenAsync(request, channel.WithLogging(this.loggerFactory, marker), ct)
                .ConfigureAwait(false);
            this.logger.LogInformation("Transport channel open; attempt {Attempt}; outcome {Outcome}.",
                marker, session is null ? "no-lease" : "accepted");
            return session;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException
            or IOException or TransportException or TimeoutException)
        {
            this.logger.LogWarning("Transport channel open failed; attempt {Attempt}; outcome {Outcome}.",
                marker, error is OperationCanceledException ? "cancelled" : error is TimeoutException ? "timeout" : "failure");
            throw;
        }
    }

    public async Task<IAsyncDisposable?> OnStreamOpenAsync(
        JsonElement request,
        Stream stream,
        CancellationToken ct = default)
    {
        var marker = TransportMetadataTrace.MarkerForRequest(request);
        this.logger.LogInformation("Transport stream open; attempt {Attempt}; frame {FrameType}.",
            marker, TransportMetadataTrace.FrameType(request));
        try
        {
            return await this.inner.OnStreamOpenAsync(
                request, stream.WithLogging(this.loggerFactory, marker), ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException
            or IOException or TransportException or TimeoutException)
        {
            this.logger.LogWarning("Transport stream open failed; attempt {Attempt}; outcome {Outcome}.",
                marker, error is OperationCanceledException ? "cancelled" : "failure");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.logger.LogInformation("Transport listener closing.");
        try
        {
            await this.inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            this.logger.LogWarning("Transport listener close faulted.");
            throw;
        }
    }
}
