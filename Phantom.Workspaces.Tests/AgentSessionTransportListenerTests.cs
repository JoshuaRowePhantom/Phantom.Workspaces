using System.Text.Json;
using System.Threading.Channels;
using Moq;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionTransportListenerTests
{
    [Fact]
    public async Task OnChannelOpenAsync_OtherType_ReturnsNull()
    {
        await using var listener = Listener(out _, out _);
        await using var channel = new TestChannel();
        Assert.Null(await listener.OnChannelOpenAsync(
            JsonSerializer.SerializeToElement(new { type = "other" }), channel, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnStreamOpenAsync_AnyRequest_ReturnsNull()
    {
        await using var listener = Listener(out _, out _);
        Assert.Null(await listener.OnStreamOpenAsync(
            JsonSerializer.SerializeToElement(new { type = "attach-agent-session" }),
            Stream.Null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnChannelOpenAsync_MalformedRequest_WritesSanitizedTerminalError()
    {
        await using var listener = Listener(out _, out _);
        await using var channel = new TestChannel();
        await listener.OnChannelOpenAsync(
            JsonSerializer.SerializeToElement(new { type = "attach-agent-session", secret = "local-path" }),
            channel,
            TestContext.Current.CancellationToken);
        var error = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("local-path", error.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("invalid-request", error.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnChannelOpenAsync_UnauthenticatedChannel_DoesNotLookupSession()
    {
        await using var listener = Listener(out var registry, out _);
        await using var channel = new TestChannel();
        await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(Open()), channel, TestContext.Current.CancellationToken);
        registry.Verify(value => value.TryGetAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static AgentSessionTransportListener Listener(
        out Mock<IRemoteAgentSessionRuntimeRegistry> registry,
        out Mock<IAgentSessionRuntimeHostFactory> factory)
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        factory = new Mock<IAgentSessionRuntimeHostFactory>();
        var provider = new Mock<ITransportPeerIdentityProvider>();
        provider.Setup(value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>()))
            .Throws(new UnauthorizedAccessException());
        return new AgentSessionTransportListener(
            new RemoteAgentSessionHost(authorizer.Object, registry.Object, factory.Object), provider.Object);
    }

    private static AgentSessionOpenRequest Open() => new()
    {
        ProtocolVersion = 1,
        AgentSessionId = "session",
        ExpectedOwningProfileEntityId = Guid.NewGuid().ToString(),
        ExpectedOwnershipGeneration = 1,
        OpenIntent = AgentSessionOpenIntent.Attach,
        AttachmentToken = Guid.NewGuid().ToString("N"),
        Capabilities = [],
    };

    private sealed class TestChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.channel.Writer;
        public ChannelReader<JsonElement> Reader => this.channel.Reader;
        public ValueTask DisposeAsync() { this.channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
