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
}
