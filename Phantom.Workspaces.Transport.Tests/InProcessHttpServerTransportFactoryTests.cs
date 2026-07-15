using System.Text.Json;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class InProcessHttpServerTransportFactoryTests
{
    [Fact]
    public async Task InProcessHttpServerTransportFactory_AcceptTransport_RoutesChannelOpen()
    {
        await using var factory = new InProcessHttpServerTransportFactory();
        var listener = new RecordingListener();
        factory.Registry.Register(listener);
        var (server, client) = InProcessTransport.Create(factory.Registry);

        await factory.AcceptAsync(server);
        using var request = JsonDocument.Parse("""{"target":"listener"}""");
        var channel = await client.ConnectToMessageChannelAsync(request.RootElement);

        var opened = await listener.Channel.Task;
        Assert.NotNull(opened);
        Assert.Equal("listener", listener.Request.GetProperty("target").GetString());
        Assert.Equal(1, factory.ActiveTransportCount);

        await channel.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task InProcessHttpServerTransportFactory_LeaseTimer_FiresAfterConfiguredDuration()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = new InProcessHttpServerTransportFactory(TimeSpan.FromMinutes(1), () => now);
        var transport = new DisposableTransport();
        await factory.AcceptAsync(transport);

        now = now.AddMinutes(1);
        await factory.SweepExpiredLeasesAsync();

        Assert.True(transport.Disposed);
        Assert.Equal(0, factory.ActiveTransportCount);
    }

    private sealed class RecordingListener : ITransportListener
    {
        public TaskCompletionSource<IMessageChannel> Channel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonElement Request { get; private set; }

        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            this.Request = request.Clone();
            this.Channel.SetResult(channel);
            return Task.FromResult<IAsyncDisposable?>(new NoopLease());
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposableTransport : ITransport
    {
        public bool Disposed { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
