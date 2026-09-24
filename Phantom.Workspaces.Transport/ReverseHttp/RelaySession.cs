using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Transport.Logging;
using System.Diagnostics;

namespace Phantom.Workspaces.Transport.ReverseHttp;

internal sealed class RelaySession : IAsyncDisposable
{
    private readonly IMessageChannel first;
    private readonly IMessageChannel second;
    private readonly CancellationTokenSource shutdown;
    private readonly Task firstToSecond;
    private readonly Task secondToFirst;
    private int shutdownStarted;
    private readonly ILogger logger;
    private readonly bool traceMetadata;
    private readonly ConcurrentDictionary<string, string> attempts = new(StringComparer.Ordinal);

    public RelaySession(
        IMessageChannel first, IMessageChannel second, CancellationToken cancellationToken = default,
        ILogger? logger = null, bool traceMetadata = false)
    {
        this.first = first;
        this.second = second;
        this.logger = logger ?? NullLogger.Instance;
        this.traceMetadata = traceMetadata;
        this.shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.firstToSecond = this.PumpAsync(first, second);
        this.secondToFirst = this.PumpAsync(second, first);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.shutdownStarted, 1) == 0)
        {
            await this.WriteCloseAsync(this.first).ConfigureAwait(false);
            await this.WriteCloseAsync(this.second).ConfigureAwait(false);
        }

        this.shutdown.Cancel();
        await this.WhenPumpsCompleteAsync().ConfigureAwait(false);
        await this.DisposeChannelsAsync().ConfigureAwait(false);
        this.shutdown.Dispose();
    }

    private async Task PumpAsync(IMessageChannel source, IMessageChannel destination)
    {
        try
        {
            await foreach (var frame in source.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                var started = Stopwatch.GetTimestamp();
                await destination.Writer.WriteAsync(frame.Clone(), this.shutdown.Token).ConfigureAwait(false);
                if (this.traceMetadata && this.logger.IsEnabled(LogLevel.Debug))
                {
                    var marker = this.MarkerFor(frame);
                    this.logger.LogDebug(
                        "Reverse relay hop {Direction}; attempt {Attempt}; frame {FrameType}; bytes {Bytes}; outcome written-to-hop; elapsed {ElapsedMilliseconds}ms.",
                        ReferenceEquals(source, this.first) ? "caller-to-worker" : "worker-to-caller",
                        marker, TransportMetadataTrace.FrameType(frame),
                        TransportMetadataTrace.ByteCount(frame),
                        Math.Clamp((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue));
                }
            }

        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            await this.ShutdownFromClosedSideAsync(destination).ConfigureAwait(false);
        }
    }

    private string MarkerFor(JsonElement frame)
    {
        if (frame.ValueKind == JsonValueKind.Object
            && frame.TryGetProperty("channelId", out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } id)
        {
            if (TransportMetadataTrace.FrameType(frame) == "channel-open")
                return this.attempts.GetOrAdd(id, _ => TransportMetadataTrace.MarkerForRequest(frame));
            if (TransportMetadataTrace.FrameType(frame) == "channel-close"
                && this.attempts.TryRemove(id, out var closedMarker))
                return closedMarker;
            if (this.attempts.TryGetValue(id, out var marker))
                return marker;
        }

        return TransportMetadataTrace.NewMarker();
    }

    private async ValueTask ShutdownFromClosedSideAsync(IMessageChannel stillConnected)
    {
        if (Interlocked.Exchange(ref this.shutdownStarted, 1) != 0)
        {
            return;
        }

        this.logger.LogInformation("Reverse relay closed; outcome disconnected.");
        await this.WriteCloseAsync(stillConnected).ConfigureAwait(false);
        this.shutdown.Cancel();
        await this.DisposeChannelsAsync().ConfigureAwait(false);
    }

    private async ValueTask WhenPumpsCompleteAsync()
    {
        try
        {
            await Task.WhenAll(this.firstToSecond, this.secondToFirst).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
        }
    }

    private async ValueTask DisposeChannelsAsync()
    {
        await this.first.DisposeAsync().ConfigureAwait(false);
        await this.second.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask WriteCloseAsync(IMessageChannel channel)
    {
        try
        {
            using var document = JsonDocument.Parse("""{"type":"channel-close"}""");
            await channel.Writer.WriteAsync(document.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
