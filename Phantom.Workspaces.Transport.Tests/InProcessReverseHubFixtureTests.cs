using System.Text.Json;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class InProcessReverseHubFixtureTests
{
    [Fact]
    public async Task InProcessReverseHubFixture_SimulateRegistration_StoresEntityId()
    {
        await using var fixture = new InProcessReverseHubFixture();
        var entityId = Guid.NewGuid();

        await fixture.SimulateClientRegistrationAsync(entityId);

        Assert.True(fixture.ReverseHttpServer.IsRegistered(entityId.ToString("D")));
    }

    [Fact]
    public async Task InProcessReverseHubFixture_SimulateRegistration_ReturnsClientTransport()
    {
        await using var fixture = new InProcessReverseHubFixture();
        var entityId = Guid.NewGuid();
        await fixture.SimulateClientRegistrationAsync(entityId);
        var forwardingClient = await fixture.CreateForwardingClientAsync();
        using var relayRequest = JsonDocument.Parse($$"""{"type":"reverse-http","entity-id":"{{entityId:D}}"}""");

        var relayChannel = await forwardingClient.ConnectToMessageChannelAsync(relayRequest.RootElement);

        Assert.NotNull(relayChannel);
    }

    [Fact]
    public async Task InProcessReverseHubFixture_RelaySetup_PumpForwardsBtoC()
    {
        await using var fixture = new InProcessReverseHubFixture();
        var entityId = Guid.NewGuid();
        var machineC = await fixture.SimulateClientRegistrationAsync(entityId);
        var forwardingClient = await fixture.CreateForwardingClientAsync();
        using var relayRequest = JsonDocument.Parse($$"""{"type":"reverse-http","entity-id":"{{entityId:D}}"}""");
        var machineBRelay = await forwardingClient.ConnectToMessageChannelAsync(relayRequest.RootElement);
        using var channelOpen = JsonDocument.Parse("""{"type":"channel-open","request":{"type":"chat-client"}}""");

        await machineBRelay.Writer.WriteAsync(channelOpen.RootElement);
        var registrationMessage = await fixture.LastClientRegistrationChannel!.Reader.ReadAsync();

        Assert.Equal(channelOpen.RootElement.GetRawText(), registrationMessage.GetRawText());
    }

    [Fact]
    public async Task InProcessReverseHubFixture_Dispose_CleansUpAllTransports()
    {
        var fixture = new InProcessReverseHubFixture();
        var entityId = Guid.NewGuid();
        await fixture.SimulateClientRegistrationAsync(entityId);
        Assert.True(fixture.ReverseHttpServer.IsRegistered(entityId.ToString("D")));

        await fixture.DisposeAsync();

        Assert.Equal(0, fixture.HttpServer.ActiveTransportCount);
        Assert.False(fixture.ReverseHttpServer.IsRegistered(entityId.ToString("D")));
    }
}
