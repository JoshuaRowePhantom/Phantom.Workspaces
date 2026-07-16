using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseHttpServerTransportFactoryTests
{
    [Fact]
    public async Task ReverseHttpServerTransportFactory_Registration_StoresChannel()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var request = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");

        var lease = await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        Assert.NotNull(lease);
        Assert.True(factory.IsRegistered("machine-c"));
        Assert.Equal(1, factory.RegistrationCount);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_Registration_Dispose_RemovesChannel()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var request = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        var lease = await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        await lease!.DisposeAsync();

        Assert.False(factory.IsRegistered("machine-c"));
        Assert.Equal(0, factory.RegistrationCount);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_Lookup_NotRegistered_SendsError()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var channel = new ReverseHttpClientTransportFactoryTests.FakeMessageChannel();
        using var request = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"missing-machine"}""");

        var lease = await factory.OnChannelOpenAsync(request.RootElement, channel);
        await lease!.DisposeAsync();
        var error = await channel.Reader.ReadAsync();

        Assert.Equal("channel-open-error", error.GetProperty("type").GetString());
        Assert.Equal("not-registered", error.GetProperty("error-code").GetString());
        Assert.True(channel.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_RelayPump_FramesFromB_ForwardedToC()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var machineC = new RelayTestMessageChannel();
        var machineB = new RelayTestMessageChannel();
        using var registerRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        await using var registration = await factory.OnChannelOpenAsync(registerRequest.RootElement, machineC);
        using var relayRequest = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        await using var relay = await factory.OnChannelOpenAsync(relayRequest.RootElement, machineB);
        using var frame = JsonDocument.Parse("""{"type":"channel-open","request":{"type":"chat-client"},"opaque":[1,2,3]}""");

        await machineB.DeliverAsync(frame.RootElement);
        var forwarded = await machineC.Sent.ReadAsync();

        Assert.Equal(frame.RootElement.GetRawText(), forwarded.GetRawText());
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_RelayPump_FramesFromC_ForwardedToB()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var machineC = new RelayTestMessageChannel();
        var machineB = new RelayTestMessageChannel();
        using var registerRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        await using var registration = await factory.OnChannelOpenAsync(registerRequest.RootElement, machineC);
        using var relayRequest = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        await using var relay = await factory.OnChannelOpenAsync(relayRequest.RootElement, machineB);
        using var frame = JsonDocument.Parse("""{"type":"channel-message","payload":{"text":"hello"}}""");

        await machineC.DeliverAsync(frame.RootElement);
        var relayAck = await machineB.Sent.ReadAsync();
        var forwarded = await machineB.Sent.ReadAsync();

        Assert.Equal("channel-open-ack", relayAck.GetProperty("type").GetString());
        Assert.Equal(frame.RootElement.GetRawText(), forwarded.GetRawText());
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_RelayPump_OneSideCloses_OtherReceivesChannelClose()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var machineC = new RelayTestMessageChannel();
        var machineB = new RelayTestMessageChannel();
        using var registerRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        await using var registration = await factory.OnChannelOpenAsync(registerRequest.RootElement, machineC);
        using var relayRequest = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        await using var relay = await factory.OnChannelOpenAsync(relayRequest.RootElement, machineB);

        machineC.CompleteInbound();
        var relayAck = await machineB.Sent.ReadAsync();
        var close = await machineB.Sent.ReadAsync();
        await machineB.Sent.Completion;

        Assert.Equal("channel-open-ack", relayAck.GetProperty("type").GetString());
        Assert.Equal("channel-close", close.GetProperty("type").GetString());
        Assert.True(machineB.Disposed);
        Assert.True(machineC.Disposed);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_RelayPump_Dispose_CancelsAndDisposesBothChannels()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var machineC = new RelayTestMessageChannel();
        var machineB = new RelayTestMessageChannel();
        using var registerRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"machine-c"}""");
        await using var registration = await factory.OnChannelOpenAsync(registerRequest.RootElement, machineC);
        using var relayRequest = JsonDocument.Parse("""{"type":"reverse-http","entity-id":"machine-c"}""");
        var relay = await factory.OnChannelOpenAsync(relayRequest.RootElement, machineB);

        await relay!.DisposeAsync();

        Assert.True(machineB.Disposed);
        Assert.True(machineC.Disposed);
        Assert.Equal("channel-open-ack", (await machineB.Sent.ReadAsync()).GetProperty("type").GetString());
        Assert.Equal("channel-close", (await machineB.Sent.ReadAsync()).GetProperty("type").GetString());
        Assert.Equal("channel-close", (await machineC.Sent.ReadAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_MultipleRegistrations_IndependentSlots()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var firstRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"first-machine"}""");
        using var secondRequest = JsonDocument.Parse("""{"type":"reverse-register","entity-id":"second-machine"}""");
        var firstLease = await factory.OnChannelOpenAsync(firstRequest.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());
        var secondLease = await factory.OnChannelOpenAsync(secondRequest.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        await firstLease!.DisposeAsync();

        Assert.False(factory.IsRegistered("first-machine"));
        Assert.True(factory.IsRegistered("second-machine"));
        Assert.Equal(1, factory.RegistrationCount);

        await secondLease!.DisposeAsync();
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_OtherDescriptors_ReturnsNull()
    {
        var factory = new ReverseHttpServerTransportFactory();
        using var request = JsonDocument.Parse("""{"type":"http","entity-id":"machine-c"}""");

        var lease = await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());

        Assert.Null(lease);
        Assert.Equal(0, factory.RegistrationCount);
    }

    [Fact]
    public async Task ReverseHttpServerTransportFactory_ConcurrentAccess_ThreadSafe()
    {
        var factory = new ReverseHttpServerTransportFactory();
        var leases = await Task.WhenAll(Enumerable.Range(0, 50).Select(async index =>
        {
            using var request = JsonDocument.Parse($$"""{"type":"reverse-register","entity-id":"machine-{{index}}"}""");
            return await factory.OnChannelOpenAsync(request.RootElement, new ReverseHttpClientTransportFactoryTests.FakeMessageChannel());
        }));

        Assert.Equal(50, factory.RegistrationCount);

        await Task.WhenAll(leases.Select(static lease => lease!.DisposeAsync().AsTask()));

        Assert.Equal(0, factory.RegistrationCount);
    }

    private sealed class RelayTestMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> sent = Channel.CreateUnbounded<JsonElement>();

        public bool Disposed { get; private set; }

        public ChannelWriter<JsonElement> Writer => this.sent.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ChannelReader<JsonElement> Sent => this.sent.Reader;

        public ValueTask DeliverAsync(JsonElement frame) => this.inbound.Writer.WriteAsync(frame.Clone());

        public void CompleteInbound() => this.inbound.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.inbound.Writer.TryComplete();
            this.sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
