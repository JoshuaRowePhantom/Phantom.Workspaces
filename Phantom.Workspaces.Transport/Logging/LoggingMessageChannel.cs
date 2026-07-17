using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Transport.Logging;

/// <summary>
/// Decorator for <see cref="IMessageChannel"/> that logs each message sent and received, plus close
/// and error events, by wrapping the inner <see cref="ChannelWriter{T}"/>/<see cref="ChannelReader{T}"/>.
/// The concrete channel contains no logging.
/// </summary>
internal sealed class LoggingMessageChannel : IMessageChannel
{
    private readonly IMessageChannel inner;
    private readonly ILogger logger;

    public LoggingMessageChannel(IMessageChannel inner, ILogger logger)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.Writer = new LoggingChannelWriter(inner.Writer, logger);
        this.Reader = new LoggingChannelReader(inner.Reader, logger);
    }

    public ChannelWriter<JsonElement> Writer { get; }

    public ChannelReader<JsonElement> Reader { get; }

    public async ValueTask DisposeAsync()
    {
        this.logger.LogInformation("Transport channel closing.");
        try
        {
            await this.inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Transport channel close faulted.");
            throw;
        }
    }

    private sealed class LoggingChannelWriter(ChannelWriter<JsonElement> inner, ILogger logger)
        : ChannelWriter<JsonElement>
    {
        public override bool TryComplete(Exception? error = null)
        {
            if (error is not null)
            {
                logger.LogWarning(error, "Transport channel writer completed with error.");
            }

            return inner.TryComplete(error);
        }

        public override bool TryWrite(JsonElement item)
        {
            var written = inner.TryWrite(item);
            if (written)
            {
                logger.LogInformation("Transport message sent: {Message}", item.GetRawText());
            }

            return written;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => inner.WaitToWriteAsync(cancellationToken);

        public override async ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Transport message sent: {Message}", item.GetRawText());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Transport message send failed.");
                throw;
            }
        }
    }

    private sealed class LoggingChannelReader(ChannelReader<JsonElement> inner, ILogger logger)
        : ChannelReader<JsonElement>
    {
        public override Task Completion => inner.Completion;

        public override bool CanCount => inner.CanCount;

        public override bool CanPeek => inner.CanPeek;

        public override int Count => inner.Count;

        public override bool TryRead(out JsonElement item)
        {
            var read = inner.TryRead(out item);
            if (read)
            {
                logger.LogInformation("Transport message received: {Message}", item.GetRawText());
            }

            return read;
        }

        public override bool TryPeek(out JsonElement item) => inner.TryPeek(out item);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => inner.WaitToReadAsync(cancellationToken);

        public override async ValueTask<JsonElement> ReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var item = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Transport message received: {Message}", item.GetRawText());
                return item;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Transport message receive failed.");
                throw;
            }
        }
    }
}
