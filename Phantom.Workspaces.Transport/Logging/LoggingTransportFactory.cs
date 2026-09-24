using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Transport.Logging;

internal sealed class LoggingTransportFactory(ITransportFactory inner, ILoggerFactory loggerFactory)
    : ITransportFactory
{
    public async Task<ITransport?> ConnectToAsync(JsonElement descriptor, CancellationToken ct = default)
    {
        var transport = await inner.ConnectToAsync(descriptor, ct).ConfigureAwait(false);
        return transport is null ? null : new LoggingTransport(transport, loggerFactory);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private sealed class LoggingTransport(ITransport inner, ILoggerFactory loggerFactory) : ITransport
    {
        private readonly ILogger logger = loggerFactory.CreateLogger<LoggingTransportFactory>();

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request, CancellationToken ct = default)
        {
            var marker = TransportMetadataTrace.MarkerForRequest(request);
            this.logger.LogInformation(
                "Transport caller channel open; attempt {Attempt}; frame {FrameType}; outcome started.",
                marker, TransportMetadataTrace.FrameType(request));
            try
            {
                var channel = await inner.ConnectToMessageChannelAsync(request, ct).ConfigureAwait(false);
                this.logger.LogInformation(
                    "Transport caller channel open; attempt {Attempt}; outcome transport-connected.",
                    marker);
                return channel.WithLogging(loggerFactory, marker);
            }
            catch (OperationCanceledException)
            {
                this.logger.LogWarning("Transport caller channel open; attempt {Attempt}; outcome cancelled.", marker);
                throw;
            }
            catch (TimeoutException)
            {
                this.logger.LogWarning("Transport caller channel open; attempt {Attempt}; outcome timeout.", marker);
                throw;
            }
            catch (TransportException)
            {
                this.logger.LogWarning("Transport caller channel open; attempt {Attempt}; outcome transport-failure.", marker);
                throw;
            }
        }

        public async Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
        {
            var marker = TransportMetadataTrace.MarkerForRequest(request);
            this.logger.LogInformation(
                "Transport caller stream open; attempt {Attempt}; frame {FrameType}; outcome started.",
                marker, TransportMetadataTrace.FrameType(request));
            var stream = await inner.ConnectToStreamAsync(request, ct).ConfigureAwait(false);
            this.logger.LogInformation(
                "Transport caller stream open; attempt {Attempt}; outcome transport-connected.",
                marker);
            return stream.WithLogging(loggerFactory, marker);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
