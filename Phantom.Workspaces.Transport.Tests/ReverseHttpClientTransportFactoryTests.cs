using System.Text.Json;
using System.Threading.Channels;
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

        public bool Disposed { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.ChannelRequests.Add(request.Clone());
            return Task.FromResult<IMessageChannel>(new FakeMessageChannel());
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
}
