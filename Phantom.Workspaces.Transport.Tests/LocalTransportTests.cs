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

    [Fact]
    public async Task LocalTransport_ConnectToStreamAsync_WaitsForStreamListener()
    {
        // Verifies ConnectToStreamAsync does not complete until the stream listener
        // has accepted the request and returned its lease. Before the fix, the client
        // stream was returned immediately while listener.OnStreamOpenAsync ran on a
        // background task, causing first-write races.
        var listenerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new TransportRegistry();
        registry.Register(new DelayedStreamListener(listenerStarted, proceedToComplete));
        await using var transport = new LocalTransport(registry);

        var connectTask = transport.ConnectToStreamAsync(JsonDocument.Parse("{}").RootElement);

        // Wait for the listener to be invoked. If ConnectToStreamAsync completed immediately
        // (before the fix), this await would hang because the listener never gets called.
        await listenerStarted.Task;

        // The listener is now running but blocked. ConnectToStreamAsync should not be complete yet.
        Assert.False(connectTask.IsCompleted, "ConnectToStreamAsync should not complete before listener returns.");

        // Release the listener so it can return.
        proceedToComplete.SetResult(true);

        // Now the connect completes and returns a stream ready for I/O.
        await using var stream = await connectTask;
        Assert.NotNull(stream);
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

    private sealed class DelayedStreamListener(
        TaskCompletionSource<bool> listenerStarted,
        TaskCompletionSource<bool> proceedToComplete) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public async Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            // Signal that the listener has been invoked.
            listenerStarted.SetResult(true);
            // Wait for the test to verify the connect is not complete, then proceed.
            await proceedToComplete.Task;
            return new DummyDisposable();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
