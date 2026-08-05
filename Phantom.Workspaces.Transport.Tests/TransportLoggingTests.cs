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
            e => (e.Level == LogLevel.Error || e.Level == LogLevel.Warning) && e.Exception is InvalidOperationException);
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
}
