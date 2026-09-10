using System.Text.Json;
using System.Threading.Channels;
using System.Collections.ObjectModel;
using AgentSchema;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
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
        var error = await channel.Output.ReadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("local-path", error.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("invalid-request", error.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnChannelOpenAsync_ValidAttach_ReturnsAttachmentLease()
    {
        var chat = Chat();
        await using var runtime = new RemoteAgentSessionLease(
            "session", 1, Epoch, chat.Object, true, Snapshot);
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.TryGetAsync("session", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(runtime);
        var authorizer = AllowingAuthorizer();
        var provider = new Mock<ITransportPeerIdentityProvider>();
        provider.Setup(value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>())).Returns(Peer());
        await using var listener = new AgentSessionTransportListener(
            new RemoteAgentSessionHost(authorizer.Object, registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>()),
            provider.Object);
        await using var channel = new TestChannel();
        var lease = await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(Open()), channel, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        Assert.Contains("session-snapshot", (await channel.Output.ReadAsync(
            TestContext.Current.CancellationToken)).GetRawText(), StringComparison.Ordinal);
        await lease.DisposeAsync();
        Assert.Equal(0, runtime.ViewerCount);
    }

    [Fact]
    public async Task OnChannelOpenAsync_UnauthorizedPeer_DoesNotRevealSessionExistence()
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = false });
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        var provider = new Mock<ITransportPeerIdentityProvider>();
        provider.Setup(value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>())).Returns(Peer());
        await using var listener = new AgentSessionTransportListener(
            new RemoteAgentSessionHost(authorizer.Object, registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>()),
            provider.Object);
        await using var channel = new TestChannel();
        await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(Open()), channel, TestContext.Current.CancellationToken);
        var wire = (await channel.Output.ReadAsync(TestContext.Current.CancellationToken)).GetRawText();
        Assert.Contains("not-found", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-session-id", wire, StringComparison.OrdinalIgnoreCase);
        registry.Verify(value => value.TryGetAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_ActiveAttachments_ReleasesViewersThenHostStopsAllRuntimes()
    {
        var runtime = new RemoteAgentSessionLease("session", 1, Epoch, Chat().Object, true, Snapshot);
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(runtime), TestContext.Current.CancellationToken);
        var provider = new Mock<ITransportPeerIdentityProvider>();
        provider.Setup(value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>())).Returns(Peer());
        var listener = new AgentSessionTransportListener(
            new RemoteAgentSessionHost(AllowingAuthorizer().Object, registry, Mock.Of<IAgentSessionRuntimeHostFactory>()),
            provider.Object);
        await using var channel = new TestChannel();
        Assert.NotNull(await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(Open()), channel, TestContext.Current.CancellationToken));
        await listener.DisposeAsync();
        Assert.Equal(0, runtime.ViewerCount);
        Assert.True(runtime.IsFenced);
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

    [Fact]
    public async Task OnChannelOpenAsync_Takeover_AuthorizesPersistsAndAcknowledges()
    {
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.TryGetAsync(
                "session", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteAgentSessionLease?)null);
        var replacement = Intent() with
        {
            OwningProfileEntityId = "22222222-2222-2222-2222-222222222222",
            OwnershipGeneration = 2,
        };
        var runtime = new RemoteAgentSessionLease(
            "session", 2, Epoch, Chat().Object, false, Snapshot);
        registry.Setup(value => value.GetOrStartAsync(
                replacement, It.IsAny<Func<CancellationToken, Task<RemoteAgentSessionLease>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(runtime);
        var factory = new Mock<IAgentSessionRuntimeHostFactory>();
        factory.Setup(value => value.TryTakeOverAsync(
                It.IsAny<AgentSessionTakeoverRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        factory.Setup(value => value.LoadIntentAsync("session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(replacement);
        var provider = new Mock<ITransportPeerIdentityProvider>();
        provider.Setup(value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>())).Returns(Peer());
        await using var listener = new AgentSessionTransportListener(
            new RemoteAgentSessionHost(AllowingAuthorizer().Object, registry.Object, factory.Object),
            provider.Object);
        await using var channel = new TestChannel();
        var request = new AgentSessionTakeoverRequest
        {
            AgentSessionId = "session",
            ExpectedOwningProfileEntityId = Open().ExpectedOwningProfileEntityId,
            ExpectedOwnershipGeneration = 1,
            NewOwningProfileEntityId = replacement.OwningProfileEntityId,
            CorrelationId = Guid.NewGuid(),
        };

        Assert.Null(await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeTakeover(request),
            channel,
            TestContext.Current.CancellationToken));

        var frame = AgentSessionProtocolCodec.DeserializeFrame(
            await channel.Output.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(request.CorrelationId, frame.CorrelationId);
        Assert.IsType<CommandCompletedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame));
        factory.Verify(value => value.TryTakeOverAsync(
            It.Is<AgentSessionTakeoverRequest>(actual =>
                actual.NewOwningProfileEntityId == replacement.OwningProfileEntityId),
            It.IsAny<CancellationToken>()), Times.Once);
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

    private static readonly RuntimeEpoch Epoch = new() { Value = Guid.NewGuid() };

    private static TransportPeerIdentity Peer() => new()
    {
        AuthenticationScheme = "test", StablePeerId = "peer", UserEntityId = Guid.NewGuid().ToString(),
    };

    private static Mock<IAgentSessionAttachAuthorizer> AllowingAuthorizer()
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = true });
        return authorizer;
    }

    private static Mock<IAgentChat> Chat()
    {
        var queues = new Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot
        {
            Revision = 0, Queues = [],
        });
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.InputQueues).Returns(queues.Object);
        chat.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        chat.SetupGet(value => value.SubAgents).Returns(
            new ReadOnlyObservableCollection<IRunningSubAgent>(new ObservableCollection<IRunningSubAgent>()));
        chat.SetupGet(value => value.Modals).Returns(
            new ReadOnlyObservableCollection<AgentChatModal>(new ObservableCollection<AgentChatModal>()));
        chat.Setup(value => value.GetToolSnapshot()).Returns([]);
        chat.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return chat;
    }

    private static AgentSessionSnapshot Snapshot() => new()
    {
        Information = new AgentInformation
        {
            AgentSessionId = "session", AgentId = "agent", Name = "agent", DisplayName = "Agent",
            Description = "Description", AcceptsUserInput = true,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                """{"kind":"prompt","name":"agent","model":{"id":"echo","provider":"echo","apiType":"Echo"}}"""),
        },
        Usage = new Usage(),
        InputQueues = new AgentInputQueuesSnapshot { Revision = 0, Queues = [] },
        IsBusy = false,
        History = [],
        RunningItems = [],
        Tools = [],
        Subagents = [],
        Modals = [],
        ContinueInBackground = false,
        ViewerCount = 0,
    };

    private static PersistedAgentSessionRuntimeIntent Intent() => new()
    {
        AgentSessionId = "session",
        OwningProfileEntityId = Open().ExpectedOwningProfileEntityId,
        OwnershipGeneration = 1,
        ExecutorBindings = new ExecutorBindings
        {
            SessionExecutor = JsonDocument.Parse("""{"type":"local"}""").RootElement.Clone(),
        },
    };

    private sealed class TestChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> input = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> output = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.output.Writer;
        public ChannelReader<JsonElement> Reader => this.input.Reader;
        public ChannelReader<JsonElement> Output => this.output.Reader;
        public ValueTask DisposeAsync()
        {
            this.input.Writer.TryComplete();
            this.output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
