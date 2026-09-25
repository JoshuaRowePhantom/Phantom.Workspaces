using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpClientTransportFactoryTests
{
    [Fact]
    public void ReverseHttpClientTransportFactory_Startup_ClearsHubUrls()
    {
        var factory = new ReverseHttpClientTransportFactory(new FakeHttpTransportFactory(), "https://hub.example", "machine-c");

        Assert.Empty(factory.HubUrls);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_Connect_UpsertsHubUrl()
    {
        var httpFactory = new FakeHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(httpFactory, "https://hub.example", "machine-c");

        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        var transport = await factory.ConnectToAsync(descriptor.RootElement);

        Assert.NotNull(transport);
        Assert.Equal(["https://hub.example"], factory.HubUrls);
        Assert.Equal("""{"type":"http","url":"https://hub.example"}""", httpFactory.ConnectionDescriptors.Single().GetRawText());
        Assert.Equal("reverse-register", httpFactory.Transports.Single().ChannelRequests.Single().GetProperty("type").GetString());
        Assert.Equal("machine-c", httpFactory.Transports.Single().ChannelRequests.Single().GetProperty("entity-id").GetString());
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_Reconnect_ReplacesHubUrl()
    {
        var httpFactory = new FakeHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(httpFactory, "https://hub.example", "machine-c");
        await factory.EnsureRegisteredAsync();

        await factory.ReconnectAsync();

        Assert.Equal(["https://hub.example"], factory.HubUrls);
        Assert.Equal(2, httpFactory.ConnectionDescriptors.Count);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_Disconnect_RemovesHubUrl()
    {
        var factory = new ReverseHttpClientTransportFactory(new FakeHttpTransportFactory(), "https://hub.example", "machine-c");
        await factory.EnsureRegisteredAsync();

        await factory.DisposeAsync();

        Assert.Empty(factory.HubUrls);
    }

    [Fact]
    public async Task ReverseHttpClientTransportFactory_InitialRegistrationFailsAfterChannelOpen_FencesChannelAndRoute()
    {
        var http = new FakeHttpTransportFactory();
        var store = new FailingOnceRouteStore();
        var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example", "11111111-1111-4111-8111-111111111111",
            store, new EntityId("22222222-2222-4222-8222-222222222222"));
        await using (factory)
        {
            await Assert.ThrowsAsync<TransportException>(() => factory.EnsureRegisteredAsync());

            Assert.False(factory.IsRegistered);
            Assert.Empty(factory.HubUrls);
            Assert.True(http.Transports.Single().Disposed);
            Assert.True(http.Transports.Single().Channels.Single().Disposed);
            Assert.Equal(1, store.Removals);

            var fresh = await factory.EnsureRegisteredAsync();
            Assert.NotSame(http.Transports[0].Channels.Single(), fresh);
            Assert.True(factory.IsRegistered);
            Assert.Equal(["https://hub.example"], factory.HubUrls);
        }
    }

    [Fact]
    public void ReverseHttpClientTransportFactory_AutoReconnect_ExponentialBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(2), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(4), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(60), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(10));
        Assert.Equal(TimeSpan.FromSeconds(30), ReverseHttpClientTransportFactory.GetReconnectDelayForAttempt(10, jitterFactor: 0.5));
    }

    internal sealed class FakeHttpTransportFactory : ITransportFactory
    {
        public List<JsonElement> ConnectionDescriptors { get; } = [];

        public List<FakeTransport> Transports { get; } = [];

        public bool Disposed { get; private set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            this.ConnectionDescriptors.Add(connectionDescriptor.Clone());
            var transport = new FakeTransport();
            this.Transports.Add(transport);
            return Task.FromResult<ITransport?>(transport);
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeTransport : ITransport
    {
        public List<JsonElement> ChannelRequests { get; } = [];

        public List<FakeMessageChannel> Channels { get; } = [];

        public bool Disposed { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.ChannelRequests.Add(request.Clone());
            var channel = new FakeMessageChannel();
            this.Channels.Add(channel);
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();

        public bool Disposed { get; private set; }

        public ChannelWriter<JsonElement> Writer => this.channel.Writer;

        public ChannelReader<JsonElement> Reader => this.channel.Reader;

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingOnceRouteStore : IReachabilityRouteStore
    {
        private bool fail = true;

        public int Removals { get; private set; }

        public Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
            EntityId profileEntityId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReachabilityRoute>>([]);

        public Task UpsertRouteAsync(
            EntityId profileEntityId, ReachabilityRoute route, CancellationToken cancellationToken = default)
        {
            if (this.fail)
            {
                this.fail = false;
                throw new IOException("Unanticipated route store failure.");
            }

            return Task.CompletedTask;
        }

        public Task RemoveRouteAsync(
            EntityId profileEntityId, string routeId, EntityId ownerProfileEntityId,
            CancellationToken cancellationToken = default)
        {
            this.Removals++;
            return Task.CompletedTask;
        }

        public Task ClearOwnedRoutesAsync(
            EntityId profileEntityId, EntityId ownerProfileEntityId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
