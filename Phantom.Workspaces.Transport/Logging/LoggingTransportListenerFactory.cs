using System;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Transport.Logging;

/// <summary>
/// Decorator for <see cref="ITransportListenerFactory"/> that auto-wraps every produced listener
/// with logging, so wrapping the whole stack requires only a single composition-time call.
/// </summary>
internal sealed class LoggingTransportListenerFactory : ITransportListenerFactory
{
    private readonly ITransportListenerFactory inner;
    private readonly ILoggerFactory loggerFactory;

    public LoggingTransportListenerFactory(ITransportListenerFactory inner, ILoggerFactory loggerFactory)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public ITransportListener CreateListener()
        => this.inner.CreateListener().WithLogging(this.loggerFactory);
}
