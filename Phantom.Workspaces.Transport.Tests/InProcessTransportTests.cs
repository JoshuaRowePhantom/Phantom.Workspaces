using System.Text.Json;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Transport.Tests;

public class InProcessTransportTests
{
    [Fact]
    public async Task InProcessTransport_Create_ReturnsConnectedPair()
    {
        var registry = new TransportRegistry();
        var listener = new EchoListener();
        registry.Register(listener);
        
        var (server, client) = InProcessTransport.Create(registry);

        var request = JsonDocument.Parse("{}").RootElement;
        var clientChannel = await client.ConnectToMessageChannelAsync(request);

        await listener.ChannelOpened.WaitAsync(TimeSpan.FromSeconds(5));

        var testMessage = JsonDocument.Parse("{\"test\":\"hello\"}").RootElement;
        await clientChannel.Writer.WriteAsync(testMessage);

        var response = await clientChannel.Reader.ReadAsync();
        Assert.Equal(testMessage.GetRawText(), response.GetRawText());

        await clientChannel.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task InProcessTransport_ChannelClose_DrainsThenCompletes()
    {
        var registry = new TransportRegistry();
        var listener = new BufferingListener();
        registry.Register(listener);

        var (server, client) = InProcessTransport.Create(registry);

        var request = JsonDocument.Parse("{}").RootElement;
        var clientChannel = await client.ConnectToMessageChannelAsync(request);

        await listener.ChannelOpened.WaitAsync(TimeSpan.FromSeconds(5));

        var msg1 = JsonDocument.Parse("{\"n\":1}").RootElement;
        var msg2 = JsonDocument.Parse("{\"n\":2}").RootElement;
        var msg3 = JsonDocument.Parse("{\"n\":3}").RootElement;

        await clientChannel.Writer.WriteAsync(msg1);
        await clientChannel.Reader.ReadAsync();
        
        await clientChannel.Writer.WriteAsync(msg2);
        await clientChannel.Reader.ReadAsync();
        
        await clientChannel.Writer.WriteAsync(msg3);
        var lastResponse = await clientChannel.Reader.ReadAsync();

        await clientChannel.DisposeAsync();

        Assert.Equal(3, listener.ReceivedCount);
        Assert.Equal("{\"ack\":true}", lastResponse.GetRawText());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    private class EchoListener : ITransportListener
    {
        private readonly TaskCompletionSource _channelOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ChannelOpened => _channelOpened.Task;

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            _ = Task.Run(async () =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(ct))
                {
                    await channel.Writer.WriteAsync(message, ct);
                }
            }, ct);

            _channelOpened.TrySetResult();
            return new DummyDisposable();
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private class DummyDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private class BufferingListener : ITransportListener
    {
        private readonly TaskCompletionSource _channelOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ChannelOpened => _channelOpened.Task;

        public int ReceivedCount { get; private set; }

        public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            _ = Task.Run(async () =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(ct))
                {
                    ReceivedCount++;
                    await channel.Writer.WriteAsync(JsonDocument.Parse("{\"ack\":true}").RootElement, ct);
                }
            }, ct);

            _channelOpened.TrySetResult();
            return new DummyDisposable();
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private class DummyDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
