using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Logging;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class TransportLoggingTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public async Task WithLogging_WrapsListener_LogsChannelOpenEvent()
    {
        var factory = new CapturingLoggerFactory();
        var listener = new FakeListener();
        var wrapped = listener.WithLogging(factory);
        await using var channel = new FakeMessageChannel();

        await wrapped.OnChannelOpenAsync(Json("""{"kind":"open"}"""), channel);

        Assert.Contains(
            factory.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("channel open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithLogging_WrapsChannel_LogsSendAndReceiveEvents()
    {
        var factory = new CapturingLoggerFactory();
        await using var inner = new FakeMessageChannel();
        var wrapped = inner.WithLogging(factory);

        await wrapped.Writer.WriteAsync(Json("""{"value":"ping"}"""));
        var received = await wrapped.Reader.ReadAsync();

        Assert.Equal("""{"value":"ping"}""", received.GetRawText());
        Assert.Contains(
            factory.Entries,
            e => e.Message.Contains("message sent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            factory.Entries,
            e => e.Message.Contains("message received", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithLogging_InnerThrows_LogsErrorAndRethrows()
    {
        var factory = new CapturingLoggerFactory();
        var listener = new FakeListener { ThrowOnChannelOpen = new InvalidOperationException("boom") };
        var wrapped = listener.WithLogging(factory);
        await using var channel = new FakeMessageChannel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => wrapped.OnChannelOpenAsync(Json("{}"), channel));

        Assert.Contains(
            factory.Entries,
            e => (e.Level == LogLevel.Error || e.Level == LogLevel.Warning)
                 && e.Exception is null && !e.Message.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithLogging_WrapsListener_DoesNotAlterBehavior()
    {
        var factory = new CapturingLoggerFactory();

        var bareListener = new FakeListener { EchoResponse = Json("""{"reply":"ok"}""") };
        await using var bareChannel = new FakeMessageChannel();
        var bareResult = await bareListener.OnChannelOpenAsync(Json("""{"n":1}"""), bareChannel);
        var bareSent = await bareChannel.ReadWrittenAsync();

        var wrappedListener = new FakeListener { EchoResponse = Json("""{"reply":"ok"}""") };
        var wrapped = wrappedListener.WithLogging(factory);
        await using var wrappedChannel = new FakeMessageChannel();
        var wrappedResult = await wrapped.OnChannelOpenAsync(Json("""{"n":1}"""), wrappedChannel);
        var wrappedSent = await wrappedChannel.ReadWrittenAsync();

        Assert.Equal(bareResult is null, wrappedResult is null);
        Assert.Equal(bareSent.GetRawText(), wrappedSent.GetRawText());
    }

    [Fact]
    public async Task WithLogging_WrapsChannel_ForwardsCloseToInner()
    {
        var factory = new CapturingLoggerFactory();
        var inner = new FakeMessageChannel();
        var wrapped = inner.WithLogging(factory);

        await wrapped.DisposeAsync();

        Assert.True(inner.Disposed);
        Assert.Contains(
            factory.Entries,
            e => e.Message.Contains("closing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithLogging_AppliedViaFactory_UsesSingleDecoratorForAllEvents()
    {
        var factory = new CapturingLoggerFactory();
        var listenerFactory = new FakeListenerFactory().WithLogging(factory);
        var listener = listenerFactory.CreateListener();
        await using var channel = new FakeMessageChannel();

        // Channel open (accept) -> inner listener writes a response on the auto-wrapped channel
        // (send) and reads the client's message (receive) -> then close.
        await channel.Writer.WriteAsync(Json("""{"client":"hi"}"""));
        await listener.OnChannelOpenAsync(Json("""{"kind":"open"}"""), channel);
        await listener.DisposeAsync();

        Assert.Contains(factory.Entries, e => e.Message.Contains("channel open", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(factory.Entries, e => e.Message.Contains("message sent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(factory.Entries, e => e.Message.Contains("message received", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(factory.Entries, e => e.Message.Contains("closing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithLogging_SensitiveMessage_LogsOnlyBoundedMetadata()
    {
        using var factory = new CapturingLoggerFactory();
        await using var inner = new FakeMessageChannel();
        await using var wrapped = inner.WithLogging(factory);
        const string secret = "private-prompt-and-token-abcdef";
        var frame = Json(
            """{"type":"channel-message","method":"tools/call","channelId":"private-session-id","payload":"private-prompt-and-token-abcdef"}""");

        await wrapped.Writer.WriteAsync(frame);
        var received = await wrapped.Reader.ReadAsync();

        Assert.Equal(frame.GetRawText(), received.GetRawText());
        var writes = factory.Entries.Where(e => e.Message.Contains("message sent", StringComparison.Ordinal));
        var reads = factory.Entries.Where(e => e.Message.Contains("message received", StringComparison.Ordinal));
        Assert.Single(writes);
        Assert.Single(reads);
        Assert.All(writes.Concat(reads), entry =>
        {
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Contains("channel-message", entry.Message, StringComparison.Ordinal);
            Assert.Contains("elapsed ", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-session-id", entry.Message, StringComparison.Ordinal);
            Assert.Contains("tools/call", entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task WithLogging_FailedWriteAndCancelledRead_ReportsSafeOutcomes()
    {
        using var factory = new CapturingLoggerFactory();
        await using var inner = new FakeMessageChannel();
        await using var wrapped = inner.WithLogging(factory);
        await using var readChannel = new FakeMessageChannel();
        await using var wrappedRead = readChannel.WithLogging(factory);
        inner.Writer.TryComplete(new InvalidOperationException("private-error-text"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await wrapped.Writer.WriteAsync(Json("""{"type":"channel-message","payload":"private-prompt"}""")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await wrappedRead.Reader.ReadAsync(cancelled.Token));

        Assert.Contains(factory.Entries, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("outcome closed", StringComparison.Ordinal));
        Assert.Contains(factory.Entries, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("cancelled", StringComparison.Ordinal));
        Assert.All(factory.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("private-error-text", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-prompt", entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task WithLogging_SensitiveStream_LogsByteCountsWithoutContent()
    {
        using var factory = new CapturingLoggerFactory();
        using var inner = new MemoryStream();
        await using var wrapped = inner.WithLogging(factory);
        var secret = System.Text.Encoding.UTF8.GetBytes("private-stream-payload");

        await wrapped.WriteAsync(secret);
        wrapped.Position = 0;
        var received = new byte[secret.Length];
        Assert.Equal(secret.Length, await wrapped.ReadAsync(received));
        Assert.Equal(secret, received);

        Assert.Contains(factory.Entries, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("stream written", StringComparison.Ordinal));
        Assert.Contains(factory.Entries, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("stream read", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Entries, entry =>
            entry.Message.Contains("private-stream-payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithLogging_JsonRpcFrame_ReportsOnlyAllowlistedMethod()
    {
        using var factory = new CapturingLoggerFactory();
        await using var inner = new FakeMessageChannel();
        await using var wrapped = inner.WithLogging(factory);

        await wrapped.Writer.WriteAsync(Json(
            """{"type":"channel-message","payload":{"jsonrpc":"2.0","method":"tools/list","params":{"token":"private-secret"}}}"""));
        await wrapped.Writer.WriteAsync(Json(
            """{"type":"channel-message","payload":{"method":"private-secret"}}"""));

        Assert.Contains(factory.Entries, entry =>
            entry.Message.Contains("method tools/list", StringComparison.Ordinal));
        Assert.Contains(factory.Entries, entry =>
            entry.Message.Contains("method other", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Entries, entry =>
            entry.Message.Contains("private-secret", StringComparison.Ordinal));
    }
}
