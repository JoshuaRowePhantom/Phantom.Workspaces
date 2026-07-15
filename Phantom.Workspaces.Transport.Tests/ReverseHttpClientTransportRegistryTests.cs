using System.Text.Json;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpClientTransportRegistryTests
{
    [Fact]
    public async Task ReverseHttpClientTransportRegistry_RoutesConnectByEntityId()
    {
        var firstHttpFactory = new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory();
        var secondHttpFactory = new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory();
        var registry = new ReverseHttpClientTransportRegistry();
        registry.Register(new ReverseHttpClientTransportFactory(firstHttpFactory, "https://first.example", "first-machine"));
        registry.Register(new ReverseHttpClientTransportFactory(secondHttpFactory, "https://second.example", "second-machine"));

        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"second-machine"}""");
        var transport = await registry.ConnectToAsync(descriptor.RootElement);

        Assert.NotNull(transport);
        Assert.Empty(firstHttpFactory.ConnectionDescriptors);
        Assert.Single(secondHttpFactory.ConnectionDescriptors);
        Assert.Equal("https://second.example", secondHttpFactory.ConnectionDescriptors.Single().GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReverseHttpClientTransportRegistry_Dispose_DisposesAllFactories()
    {
        var firstHttpFactory = new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory();
        var secondHttpFactory = new ReverseHttpClientTransportFactoryTests.FakeHttpTransportFactory();
        var registry = new ReverseHttpClientTransportRegistry();
        registry.Register(new ReverseHttpClientTransportFactory(firstHttpFactory, "https://first.example", "first-machine"));
        registry.Register(new ReverseHttpClientTransportFactory(secondHttpFactory, "https://second.example", "second-machine"));

        await registry.DisposeAsync();

        Assert.True(firstHttpFactory.Disposed);
        Assert.True(secondHttpFactory.Disposed);
        Assert.Empty(registry.Factories);
    }
}
