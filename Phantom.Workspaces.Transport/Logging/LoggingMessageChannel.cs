using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Diagnostics;
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
    private readonly string marker;

    public LoggingMessageChannel(IMessageChannel inner, ILogger logger, string marker)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.marker = marker;
        this.Writer = new LoggingChannelWriter(inner.Writer, logger, marker);
        this.Reader = new LoggingChannelReader(inner.Reader, logger, marker);
    }

    public ChannelWriter<JsonElement> Writer { get; }

    public ChannelReader<JsonElement> Reader { get; }

    internal IMessageChannel Inner => this.inner;

    public async ValueTask DisposeAsync()
    {
        this.logger.LogInformation("Transport channel closing; attempt {Attempt}.", this.marker);
        try
        {
            await this.inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ChannelClosedException)
        {
            this.logger.LogWarning("Transport channel close faulted; attempt {Attempt}.", this.marker);
            throw;
        }
    }

    private sealed class LoggingChannelWriter(ChannelWriter<JsonElement> inner, ILogger logger, string marker)
        : ChannelWriter<JsonElement>
    {
        public override bool TryComplete(Exception? error = null)
        {
            var completed = inner.TryComplete(error);
            logger.LogDebug("Transport writer complete; attempt {Attempt}; outcome {Outcome}.",
                marker, !completed ? "rejected" : error is null ? "closed" : "failed");
            return completed;
        }

        public override bool TryWrite(JsonElement item)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var written = inner.TryWrite(item);
                LogFrame(logger, "sent", marker, item, written ? "written" : "rejected", started);
                return written;
            }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException
                or InvalidOperationException or IOException or TimeoutException)
            {
                LogFrame(logger, "sent", marker, item, Category(error), started);
                throw;
            }
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => inner.WaitToWriteAsync(cancellationToken);

        public override async ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                LogFrame(logger, "sent", marker, item, "written", started);
            }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException
                or InvalidOperationException or IOException or TimeoutException)
            {
                LogFrame(logger, "sent", marker, item, Category(error), started);
                throw;
            }
        }
    }

    private sealed class LoggingChannelReader(ChannelReader<JsonElement> inner, ILogger logger, string marker)
        : ChannelReader<JsonElement>
    {
        private int terminalLogged;

        public override Task Completion => inner.Completion;

        public override bool CanCount => inner.CanCount;

        public override bool CanPeek => inner.CanPeek;

        public override int Count => inner.Count;

        public override bool TryRead(out JsonElement item)
        {
            var started = Stopwatch.GetTimestamp();
            var read = inner.TryRead(out item);
            if (read)
            {
                LogFrame(logger, "received", marker, item, "read", started);
            }

            return read;
        }

        public override bool TryPeek(out JsonElement item) => inner.TryPeek(out item);

        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var available = await inner.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                if (!available)
                    this.LogTerminal("closed");
                return available;
            }
            catch (OperationCanceledException)
            {
                this.LogTerminal("cancelled");
                throw;
            }
            catch (ChannelClosedException)
            {
                this.LogTerminal("closed");
                throw;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                this.LogTerminal("failure");
                throw;
            }
        }

        public override async ValueTask<JsonElement> ReadAsync(CancellationToken cancellationToken = default)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var item = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                LogFrame(logger, "received", marker, item, "read", started);
                return item;
            }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException
                or InvalidOperationException or IOException or TimeoutException)
            {
                logger.LogDebug("Transport message received; attempt {Attempt}; outcome {Outcome}; elapsed {ElapsedMilliseconds}ms.",
                    marker, Category(error), ElapsedMilliseconds(started));
                if (error is ChannelClosedException)
                    this.LogTerminal("closed");
                throw;
            }
        }

        private void LogTerminal(string outcome)
        {
            if (Interlocked.Exchange(ref this.terminalLogged, 1) == 0)
                logger.LogInformation("Transport receiver terminal; attempt {Attempt}; outcome {Outcome}.",
                    marker, outcome);
        }
    }

    private static void LogFrame(
        ILogger logger, string direction, string marker, JsonElement frame, string outcome, long started)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(
                "Transport message {Direction}; attempt {Attempt}; frame {FrameType}; method {Method}; bytes {Bytes}; outcome {Outcome}; elapsed {ElapsedMilliseconds}ms.",
                direction, marker, TransportMetadataTrace.FrameType(frame),
                TransportMetadataTrace.Method(frame), TransportMetadataTrace.ByteCount(frame), outcome,
                ElapsedMilliseconds(started));
    }

    private static long ElapsedMilliseconds(long started)
        => Math.Clamp((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue);

    private static string Category(Exception error) => error switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        ChannelClosedException => "closed",
        _ => "failure",
    };
}
