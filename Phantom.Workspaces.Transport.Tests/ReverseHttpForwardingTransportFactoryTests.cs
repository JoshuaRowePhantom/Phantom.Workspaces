using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpForwardingTransportFactoryTests
{
    [Fact]
    public async Task ForwardingFactory_SingleHub_ConnectsAndOpensRelayChannel()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","hub-urls":["https://hub.example"],"entity-id":"machine-c"}""");

        var connectTask = factory.ConnectToAsync(descriptor.RootElement);
        var attempt = await httpFactory.WaitForAttemptAsync("https://hub.example");
        var hubTransport = new FakeTransport();
        attempt.Succeed(hubTransport);
        var transport = await connectTask;

        Assert.NotNull(transport);
        Assert.Equal("""{"type":"http","url":"https://hub.example"}""", httpFactory.ConnectionDescriptors.Single().GetRawText());
        var relayRequest = hubTransport.ChannelRequests.Single();
        Assert.Equal("reverse-http", relayRequest.GetProperty("type").GetString());
        Assert.Equal("machine-c", relayRequest.GetProperty("entity-id").GetString());
    }

    [Fact]
    public async Task ForwardingFactory_MultipleHubs_FirstWins()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","hub-urls":["https://slow.example","https://fast.example"],"entity-id":"machine-c"}""");

        var connectTask = factory.ConnectToAsync(descriptor.RootElement);
        var slowAttempt = await httpFactory.WaitForAttemptAsync("https://slow.example");
        var fastAttempt = await httpFactory.WaitForAttemptAsync("https://fast.example");
        var fastTransport = new FakeTransport();
        fastAttempt.Succeed(fastTransport);
        var transport = await connectTask;

        Assert.NotNull(transport);
        Assert.True(slowAttempt.Cancellation.IsCancellationRequested);
        Assert.Single(fastTransport.ChannelRequests);
    }

    [Fact]
    public async Task ForwardingFactory_OneHubFails_OtherSucceeds()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","hub-urls":["https://bad.example","https://good.example"],"entity-id":"machine-c"}""");

        var connectTask = factory.ConnectToAsync(descriptor.RootElement);
        (await httpFactory.WaitForAttemptAsync("https://bad.example")).Fail(new InvalidOperationException("boom"));
        var goodAttempt = await httpFactory.WaitForAttemptAsync("https://good.example");
        var goodTransport = new FakeTransport();
        goodAttempt.Succeed(goodTransport);
        var transport = await connectTask;

        Assert.NotNull(transport);
        Assert.Single(goodTransport.ChannelRequests);
    }

    [Fact]
    public async Task ForwardingFactory_AllHubsFail_ThrowsTransportException()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","hub-urls":["https://first.example","https://second.example"],"entity-id":"machine-c"}""");

        var connectTask = factory.ConnectToAsync(descriptor.RootElement);
        (await httpFactory.WaitForAttemptAsync("https://first.example")).Fail(new InvalidOperationException("first"));
        (await httpFactory.WaitForAttemptAsync("https://second.example")).Fail(new InvalidOperationException("second"));

        var ex = await Assert.ThrowsAsync<TransportException>(async () => await connectTask);
        Assert.Contains("All reverse HTTP hub connection attempts failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardingFactory_HubTimeout_BoundedByConfiguredTimeout()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory, TimeSpan.Zero);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","hub-urls":["https://timeout.example"],"entity-id":"machine-c"}""");

        await Assert.ThrowsAsync<TransportException>(async () => await factory.ConnectToAsync(descriptor.RootElement));
    }

    [Fact]
    public async Task ForwardingFactory_NonReverseHttpDescriptor_ReturnsNull()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"http","hub-urls":["https://hub.example"],"entity-id":"machine-c"}""");

        var transport = await factory.ConnectToAsync(descriptor.RootElement);

        Assert.Null(transport);
        Assert.Empty(httpFactory.ConnectionDescriptors);
    }

    [Fact]
    public async Task ForwardingFactory_MissingHubUrls_ReturnsNull()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");

        var transport = await factory.ConnectToAsync(descriptor.RootElement);

        Assert.Null(transport);
        Assert.Empty(httpFactory.ConnectionDescriptors);
    }

    [Fact]
    public async Task ForwardingFactory_RelayNotRegistered_ThrowsTransportException()
    {
        var httpFactory = new ControllableHttpTransportFactory();
        await using var factory = new ReverseHttpForwardingTransportFactory(httpFactory);
        using var descriptor = JsonDocument.Parse("""{"type":"reverse-http","hub-urls":["https://hub.example"],"entity-id":"machine-c"}""");

        var connectTask = factory.ConnectToAsync(descriptor.RootElement);
        var attempt = await httpFactory.WaitForAttemptAsync("https://hub.example");
        attempt.Succeed(new FakeTransport(FakeMessageChannel.Kind.NotRegisteredError));

        var ex = await Assert.ThrowsAsync<TransportException>(async () => await connectTask);
        Assert.Contains("No reverse HTTP registration", ex.Message, StringComparison.Ordinal);
    }

    private sealed class ControllableHttpTransportFactory : ITransportFactory
    {
        private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private readonly List<JsonElement> connectionDescriptors = [];
        private readonly Dictionary<string, TaskCompletionSource<Attempt>> waiters = new(StringComparer.Ordinal);

        public IReadOnlyList<JsonElement> ConnectionDescriptors => this.connectionDescriptors;

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            var url = connectionDescriptor.GetProperty("url").GetString()!;
            var attempt = new Attempt(ct);
            lock (this.gate)
            {
                this.connectionDescriptors.Add(connectionDescriptor.Clone());
                this.attempts[url] = attempt;
                if (this.waiters.Remove(url, out var waiter))
                {
                    waiter.TrySetResult(attempt);
                }
            }

            return attempt.Task.WaitAsync(ct);
        }

        public Task<Attempt> WaitForAttemptAsync(string url)
        {
            lock (this.gate)
            {
                if (this.attempts.TryGetValue(url, out var attempt))
                {
                    return Task.FromResult(attempt);
                }

                var waiter = new TaskCompletionSource<Attempt>(TaskCreationOptions.RunContinuationsAsynchronously);
                this.waiters[url] = waiter;
                return waiter.Task;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Attempt
    {
        private readonly TaskCompletionSource<ITransport?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Attempt(CancellationToken cancellation) => this.Cancellation = cancellation;

        public CancellationToken Cancellation { get; }

        public Task<ITransport?> Task => this.completion.Task;

        public void Succeed(ITransport transport) => this.completion.TrySetResult(transport);

        public void Fail(Exception exception) => this.completion.TrySetException(exception);
    }

    private sealed class FakeTransport : ITransport
    {
        private readonly FakeMessageChannel.Kind relayKind;

        public FakeTransport()
            : this(FakeMessageChannel.Kind.RelayEstablished)
        {
        }

        public FakeTransport(FakeMessageChannel.Kind relayKind) => this.relayKind = relayKind;

        public List<JsonElement> ChannelRequests { get; } = [];

        public bool Disposed { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.ChannelRequests.Add(request.Clone());
            return Task.FromResult<IMessageChannel>(new FakeMessageChannel(this.relayKind));
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();

        public FakeMessageChannel(Kind kind = Kind.RelayEstablished)
        {
            var seed = kind switch
            {
                Kind.RelayEstablished => """{"type":"channel-open-ack"}""",
                Kind.NotRegisteredError => """{"type":"channel-open-error","error-code":"not-registered","message":"No reverse HTTP registration exists for 'machine-c'."}""",
                _ => null,
            };

            if (seed is not null)
            {
                using var document = JsonDocument.Parse(seed);
                this.channel.Writer.TryWrite(document.RootElement.Clone());
            }
        }

        public enum Kind
        {
            RelayEstablished,
            NotRegisteredError,
        }

        public ChannelWriter<JsonElement> Writer => this.channel.Writer;

        public ChannelReader<JsonElement> Reader => this.channel.Reader;

        public ValueTask DisposeAsync()
        {
            this.channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}