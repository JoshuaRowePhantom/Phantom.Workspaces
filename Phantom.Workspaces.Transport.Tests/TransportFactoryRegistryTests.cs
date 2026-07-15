using System.Text.Json;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Transport.Tests;

public class TransportFactoryRegistryTests
{
    [Fact]
    public async Task TransportFactoryRegistry_NullFromAllFactories_ThrowsTransportException()
    {
        var registry = new TransportFactoryRegistry();
        var factory1 = new TestTransportFactory(canHandle: false);
        var factory2 = new TestTransportFactory(canHandle: false);

        registry.Register(factory1);
        registry.Register(factory2);

        var descriptor = JsonDocument.Parse("{}").RootElement;

        await Assert.ThrowsAsync<TransportException>(async () =>
        {
            await registry.ConnectToAsync(descriptor);
        });

        Assert.True(factory1.ConnectToCalled);
        Assert.True(factory2.ConnectToCalled);
    }

    [Fact]
    public async Task TransportFactoryRegistry_FirstMatchingFactory_WinsDispatch()
    {
        var registry = new TransportFactoryRegistry();
        var factory1 = new TestTransportFactory(canHandle: false);
        var factory2 = new TestTransportFactory(canHandle: true);
        var factory3 = new TestTransportFactory(canHandle: true);

        registry.Register(factory1);
        registry.Register(factory2);
        registry.Register(factory3);

        var descriptor = JsonDocument.Parse("{}").RootElement;
        var transport = await registry.ConnectToAsync(descriptor);

        Assert.NotNull(transport);
        Assert.True(factory1.ConnectToCalled);
        Assert.True(factory2.ConnectToCalled);
        Assert.False(factory3.ConnectToCalled);
    }

    private class TestTransportFactory : ITransportFactory
    {
        private readonly bool _canHandle;

        public TestTransportFactory(bool canHandle)
        {
            _canHandle = canHandle;
        }

        public bool ConnectToCalled { get; private set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            ConnectToCalled = true;
            if (_canHandle)
            {
                var (_, client) = InProcessTransport.Create();
                return Task.FromResult<ITransport?>(client);
            }
            return Task.FromResult<ITransport?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
