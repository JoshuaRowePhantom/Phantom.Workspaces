using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Logging;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class TransportLoggingFactoryTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public async Task LoggingTransportListenerFactory_CreateListener_AutoWrapsProducedListener()
    {
        var factory = new CapturingLoggerFactory();
        var wrappedFactory = new FakeListenerFactory().WithLogging(factory);

        // No explicit per-listener WithLogging call: the factory auto-wraps.
        var listener = wrappedFactory.CreateListener();
        await using var channel = new FakeMessageChannel();
        await channel.Writer.WriteAsync(Json("""{"client":"hi"}"""));

        await listener.OnChannelOpenAsync(Json("""{"kind":"open"}"""), channel);

        Assert.Contains(
            factory.Entries,
            e => e.Message.Contains("channel open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoggingTransportListenerFactory_ProducedChannels_AreAutoWrapped()
    {
        var factory = new CapturingLoggerFactory();
        var wrappedFactory = new FakeListenerFactory().WithLogging(factory);
        var listener = wrappedFactory.CreateListener();
        await using var channel = new FakeMessageChannel();
        await channel.Writer.WriteAsync(Json("""{"client":"hi"}"""));

        // The channel handed to the factory-produced listener is auto-wrapped, so the response the
        // listener writes (and the client message it reads) are logged without any explicit wrap.
        await listener.OnChannelOpenAsync(Json("""{"kind":"open"}"""), channel);

        Assert.Contains(
            factory.Entries,
            e => e.Message.Contains("message sent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            factory.Entries,
            e => e.Message.Contains("message received", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoggingTransportListenerFactory_SensitiveOpen_AutoWrapsWithoutLeakingRequest()
    {
        using var factory = new CapturingLoggerFactory();
        var listener = new FakeListenerFactory().WithLogging(factory).CreateListener();
        await using var channel = new FakeMessageChannel();
        await channel.Writer.WriteAsync(Json("""{"type":"channel-message","payload":"private-inbound"}"""));

        await listener.OnChannelOpenAsync(
            Json("""{"type":"copilot-sdk-session","correlation-id":"59a8d519d47b4eb1a95d458e015d17ab","api-key":"private-open"}"""),
            channel);

        Assert.Contains(factory.Entries, entry =>
            entry.Message.Contains("channel open", StringComparison.Ordinal));
        Assert.Contains(factory.Entries, entry =>
            entry.Message.Contains("message received", StringComparison.Ordinal));
        Assert.All(factory.Entries, entry =>
        {
            Assert.DoesNotContain("private-open", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-inbound", entry.Message, StringComparison.Ordinal);
            Assert.Null(entry.Exception);
        });
    }
}
