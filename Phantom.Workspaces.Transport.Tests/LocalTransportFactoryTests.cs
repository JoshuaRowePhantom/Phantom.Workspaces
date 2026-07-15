using System.Text.Json;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Transport.Tests;

public class LocalTransportFactoryTests
{
    [Fact]
    public async Task LocalTransportFactory_LocalDescriptor_ReturnsTransport()
    {
        var factory = new LocalTransportFactory(new TransportRegistry());

        var transport = await factory.ConnectToAsync(JsonDocument.Parse("""{"type":"local"}""").RootElement);

        Assert.IsType<LocalTransport>(transport);
    }

    [Fact]
    public async Task LocalTransportFactory_NonLocalDescriptor_ReturnsNull()
    {
        var factory = new LocalTransportFactory(new TransportRegistry());

        var transport = await factory.ConnectToAsync(JsonDocument.Parse("""{"type":"http"}""").RootElement);

        Assert.Null(transport);
    }
}
