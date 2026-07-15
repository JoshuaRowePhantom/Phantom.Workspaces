using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Transport.Tests;

public class LocalTransportTests
{
    [Fact]
    public async Task LocalTransport_ConnectToMessageChannel_RoutesToRegistry()
    {
        var registry = new TransportRegistry();
        registry.Register(new EchoListener());
        await using var transport = new LocalTransport(registry);

        var channel = await transport.ConnectToMessageChannelAsync(JsonDocument.Parse("{}").RootElement);
        var message = JsonDocument.Parse("""{"value":"hello"}""").RootElement;
        await channel.Writer.WriteAsync(message);

        var response = await channel.Reader.ReadAsync();
        Assert.Equal(message.GetRawText(), response.GetRawText());
    }

    [Fact]
    public async Task LocalTransport_ConnectToStream_RoutesToRegistry()
    {
        var registry = new TransportRegistry();
        registry.Register(new StreamEchoListener());
        await using var transport = new LocalTransport(registry);

        await using var stream = await transport.ConnectToStreamAsync(JsonDocument.Parse("{}").RootElement);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("abc"));

        var buffer = new byte[3];
        var count = await stream.ReadAsync(buffer);

        Assert.Equal(3, count);
        Assert.Equal("abc", Encoding.UTF8.GetString(buffer));
    }

    [Fact]
    public async Task LocalTransport_NoListener_ChannelCompletesWithError()
    {
        await using var transport = new LocalTransport(new TransportRegistry());

        var channel = await transport.ConnectToMessageChannelAsync(JsonDocument.Parse("{}").RootElement);

        await Assert.ThrowsAsync<TransportException>(async () => await channel.Reader.Completion);
    }

    [Fact]
    public async Task LocalTransport_Dispose_CompletesAllChannels()
    {
        var registry = new TransportRegistry();
        registry.Register(new HoldingListener());
        var transport = new LocalTransport(registry);

        var channel = await transport.ConnectToMessageChannelAsync(JsonDocument.Parse("{}").RootElement);
        await transport.DisposeAsync();

        await channel.Reader.Completion;
    }

    [Fact]
    public async Task LocalTransport_DisposeIndividualChannel_LeavesTransportOpen()
    {
        var registry = new TransportRegistry();
        registry.Register(new EchoListener());
        await using var transport = new LocalTransport(registry);

        var first = await transport.ConnectToMessageChannelAsync(JsonDocument.Parse("{}").RootElement);
        await first.DisposeAsync();

        var second = await transport.ConnectToMessageChannelAsync(JsonDocument.Parse("{}").RootElement);
        var message = JsonDocument.Parse("""{"value":"second"}""").RootElement;
        await second.Writer.WriteAsync(message);

        var response = await second.Reader.ReadAsync();
        Assert.Equal(message.GetRawText(), response.GetRawText());
    }

    private sealed class EchoListener : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            _ = Task.Run(async () =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(ct))
                {
                    await channel.Writer.WriteAsync(message, ct);
                }
            }, ct);

            return Task.FromResult<IAsyncDisposable?>(new DummyDisposable());
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamEchoListener : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[64];
                var count = await stream.ReadAsync(buffer, ct);
                await stream.WriteAsync(buffer.AsMemory(0, count), ct);
            }, ct);

            return Task.FromResult<IAsyncDisposable?>(new DummyDisposable());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HoldingListener : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new DummyDisposable());

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new DummyDisposable());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
