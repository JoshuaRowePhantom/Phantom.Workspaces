using System.Text.Json;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Transport.Tests;

public class TransportRegistryTests
{
    [Fact]
    public async Task TransportRegistry_FirstMatchingListener_WinsDispatch()
    {
        var registry = new TransportRegistry();
        var listener1 = new TestTransportListener(shouldHandle: false);
        var listener2 = new TestTransportListener(shouldHandle: true);
        var listener3 = new TestTransportListener(shouldHandle: true);

        registry.Register(listener1);
        registry.Register(listener2);
        registry.Register(listener3);

        var request = JsonDocument.Parse("{}").RootElement;
        var (_, client) = InProcessTransport.Create();
        var channel = await client.ConnectToMessageChannelAsync(request);

        var result = await registry.OnChannelOpenAsync(request, channel);

        Assert.NotNull(result);
        Assert.True(listener1.OnChannelOpenCalled);
        Assert.True(listener2.OnChannelOpenCalled);
        Assert.False(listener3.OnChannelOpenCalled);
    }

    [Fact]
    public async Task TransportRegistry_NoListener_ReturnsNull()
    {
        var registry = new TransportRegistry();
        var listener = new TestTransportListener(shouldHandle: false);
        registry.Register(listener);

        var request = JsonDocument.Parse("{}").RootElement;
        var (_, client) = InProcessTransport.Create();
        var channel = await client.ConnectToMessageChannelAsync(request);

        var result = await registry.OnChannelOpenAsync(request, channel);

        Assert.Null(result);
        Assert.True(listener.OnChannelOpenCalled);
    }

    private class TestTransportListener : ITransportListener
    {
        private readonly bool _shouldHandle;

        public TestTransportListener(bool shouldHandle)
        {
            _shouldHandle = shouldHandle;
        }

        public bool OnChannelOpenCalled { get; private set; }
        public bool OnStreamOpenCalled { get; private set; }

        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            OnChannelOpenCalled = true;
            return Task.FromResult<IAsyncDisposable?>(_shouldHandle ? new DummyDisposable() : null);
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            OnStreamOpenCalled = true;
            return Task.FromResult<IAsyncDisposable?>(_shouldHandle ? new DummyDisposable() : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private class DummyDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
