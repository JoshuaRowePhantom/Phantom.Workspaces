using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Transport.Logging;

/// <summary>
/// The single, central entry point for applying transport logging. Wrapping any transport surface
/// with <c>WithLogging()</c> attaches a decorator that emits structured connect/accept/send/receive/
/// close/error events; no concrete transport implementation logs directly.
/// </summary>
public static class TransportLoggingExtensions
{
    /// <summary>Wraps a listener so its channel/stream open and error events are logged.</summary>
    public static ITransportListener WithLogging(this ITransportListener inner, ILoggerFactory loggerFactory)
        => new LoggingTransportListener(inner, loggerFactory);

    /// <summary>Wraps a channel so each message sent/received and close/error is logged.</summary>
    public static IMessageChannel WithLogging(this IMessageChannel inner, ILoggerFactory loggerFactory)
        => inner is LoggingMessageChannel ? inner : new LoggingMessageChannel(
            inner, loggerFactory.CreateLogger<LoggingMessageChannel>(), TransportMetadataTrace.NewMarker());

    internal static IMessageChannel WithLogging(this IMessageChannel inner, ILoggerFactory loggerFactory, string marker)
        => inner is LoggingMessageChannel ? inner : new LoggingMessageChannel(
            inner, loggerFactory.CreateLogger<LoggingMessageChannel>(), marker);

    /// <summary>Wraps stream reads and writes to report byte counts without inspecting content.</summary>
    public static Stream WithLogging(this Stream inner, ILoggerFactory loggerFactory)
        => inner is LoggingStream ? inner : new LoggingStream(
            inner, loggerFactory.CreateLogger<LoggingStream>(), TransportMetadataTrace.NewMarker());

    internal static Stream WithLogging(this Stream inner, ILoggerFactory loggerFactory, string marker)
        => inner is LoggingStream ? inner : new LoggingStream(
            inner, loggerFactory.CreateLogger<LoggingStream>(), marker);

    /// <summary>
    /// Wraps a factory so every produced listener (and its channels) is auto-wrapped, letting callers
    /// wrap the entire stack with a single composition-time call.
    /// </summary>
    public static ITransportListenerFactory WithLogging(this ITransportListenerFactory inner, ILoggerFactory loggerFactory)
        => new LoggingTransportListenerFactory(inner, loggerFactory);

    /// <summary>Logs safe caller-side channel and stream metadata on produced transports.</summary>
    public static ITransportFactory WithLogging(this ITransportFactory inner, ILoggerFactory loggerFactory)
        => inner is LoggingTransportFactory ? inner : new LoggingTransportFactory(inner, loggerFactory);
}
