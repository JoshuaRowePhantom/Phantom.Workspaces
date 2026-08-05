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
        this.logger.LogInformation("Transport channel open: {Request}", request.GetRawText());
        try
        {
            return await this.inner
                .OnChannelOpenAsync(request, channel.WithLogging(this.loggerFactory), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Transport channel open failed.");
            throw;
        }
    }

    public async Task<IAsyncDisposable?> OnStreamOpenAsync(
        JsonElement request,
        Stream stream,
        CancellationToken ct = default)
    {
        this.logger.LogInformation("Transport stream open: {Request}", request.GetRawText());
        try
        {
            return await this.inner.OnStreamOpenAsync(request, stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Transport stream open failed.");
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
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Transport listener close faulted.");
            throw;
        }
    }
}
