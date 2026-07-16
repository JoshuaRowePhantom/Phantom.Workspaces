using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class TransportReverseEndpointsTests
{
    [Fact]
    public void MapTransportReverseEndpoints_MapsTransportReverseRoute()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapTransportReverseEndpoints(
            new ReverseHttpServerTransportFactory(new ReverseConnectionStatusRegistry()),
            new ReverseConnectionStatusRegistry());

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains(
            TransportReverseEndpointRouteBuilderExtensions.TransportReverseEndpointPath,
            routePatterns);
    }

    [Fact]
    public async Task Register_KnownClientInstance_StoresRegistrationChannel()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var serverFactory = new ReverseHttpServerTransportFactory(statusRegistry);
        HostReverseEndpoints(serverFactory, statusRegistry);
        using var registerRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");

        await using var registration = await serverFactory.OnChannelOpenAsync(
            registerRequest.RootElement,
            new RelayTestMessageChannel());

        Assert.True(serverFactory.IsRegistered("machine-c"));
        var status = Assert.Single(statusRegistry.GetConnectedInstances());
        Assert.Equal("machine-c", status.ClientInstanceId);
    }

    [Fact]
    public async Task Relay_RegisteredTarget_PumpIsByteTransparent()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var serverFactory = new ReverseHttpServerTransportFactory(statusRegistry);
        HostReverseEndpoints(serverFactory, statusRegistry);
        var target = new RelayTestMessageChannel();
        var forwarding = new RelayTestMessageChannel();
        using var registerRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        await using var registration = await serverFactory.OnChannelOpenAsync(registerRequest.RootElement, target);
        using var relayRequest = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        await using var relay = await serverFactory.OnChannelOpenAsync(relayRequest.RootElement, forwarding);
        using var frame = JsonDocument.Parse("""{"type":"channel-open","request":{"type":"chat-client"},"opaque":[9,8,7]}""");

        await forwarding.DeliverAsync(frame.RootElement);
        var forwarded = await target.Sent.ReadAsync();

        Assert.Equal(frame.RootElement.GetRawText(), forwarded.GetRawText());
    }

    [Fact]
    public async Task Relay_UnknownEntityId_SendsChannelOpenErrorNotRegistered()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var serverFactory = new ReverseHttpServerTransportFactory(statusRegistry);
        HostReverseEndpoints(serverFactory, statusRegistry);
        var forwarding = new RelayTestMessageChannel();
        using var relayRequest = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"missing-machine"}""");

        var relay = await serverFactory.OnChannelOpenAsync(relayRequest.RootElement, forwarding);
        await relay!.DisposeAsync();
        var error = await forwarding.Sent.ReadAsync();

        Assert.Equal("channel-open-error", error.GetProperty("type").GetString());
        Assert.Equal("not-registered", error.GetProperty("error-code").GetString());
    }

    private static void HostReverseEndpoints(
        ReverseHttpServerTransportFactory serverFactory,
        ReverseConnectionStatusRegistry statusRegistry)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapTransportReverseEndpoints(serverFactory, statusRegistry);
    }

    private sealed class RelayTestMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> sent = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.sent.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ChannelReader<JsonElement> Sent => this.sent.Reader;

        public ValueTask DeliverAsync(JsonElement frame) => this.inbound.Writer.WriteAsync(frame.Clone());

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            this.sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
