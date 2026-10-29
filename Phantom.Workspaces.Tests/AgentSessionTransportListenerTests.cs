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
using Phantom.Workspaces.Agent.Gui;
using Microsoft.Extensions.Logging;

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
        var logger = new CapturingLogger<AgentSessionTransportListener>();
        await using var listener = Listener(out _, out _, authenticated: true, logger: logger);
        AgentSessionAttachFailure? failure = null;
        listener.AttachFailed += value => failure = value;
        await using var channel = new TestChannel();
        var attempt = Guid.NewGuid().ToString("N");
        using var request = JsonDocument.Parse($$"""
            {
              "type": "attach-agent-session",
              "diagnostic-attempt": "{{attempt}}",
              "secret": "local-path"
            }
            """);
        await listener.OnChannelOpenAsync(
            request.RootElement,
            channel,
            TestContext.Current.CancellationToken);
        var error = await channel.Output.ReadAsync(TestContext.Current.CancellationToken);
        var frame = AgentSessionProtocolCodec.DeserializeFrame(error);
        var terminal = Assert.IsType<SessionTerminalEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame));
        var diagnosticId = terminal.CompletionState.GetProperty("correlation-id").GetString();
        Assert.DoesNotContain("local-path", error.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("invalid-request", error.GetRawText(), StringComparison.Ordinal);
        Assert.NotNull(diagnosticId);
        Assert.NotNull(logger.LastLog);
        Assert.Contains(attempt, logger.LastLog.Message, StringComparison.Ordinal);
        Assert.Contains(diagnosticId, logger.LastLog.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<RemoteAgentProtocolException>(logger.LastLog.Exception);
        Assert.NotNull(failure);
        Assert.Equal("deserialize-open", failure.Stage);
        Assert.Equal("invalid-protocol", failure.Category);
        Assert.IsType<RemoteAgentProtocolException>(failure.Error);
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
        var failed = false;
        listener.AttachFailed += _ => failed = true;
        await using var channel = new TestChannel();
        var lease = await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(Open()), channel, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        Assert.Contains("session-snapshot", (await channel.Output.ReadAsync(
            TestContext.Current.CancellationToken)).GetRawText(), StringComparison.Ordinal);
        await lease.DisposeAsync();
        Assert.False(failed);
        Assert.Equal(0, runtime.ViewerCount);
        Assert.False(runtime.IsFenced);
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

    [Fact]
    public async Task OnChannelOpenAsync_BlockedTakeover_WritesStableTakeoverError()
    {
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.TryGetAsync(
                "session", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteAgentSessionLease?)null);
        var factory = new Mock<IAgentSessionRuntimeHostFactory>();
        factory.Setup(value => value.TryTakeOverAsync(
                It.IsAny<AgentSessionTakeoverRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
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
            NewOwningProfileEntityId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid(),
        };

        await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeTakeover(request),
            channel,
            TestContext.Current.CancellationToken);

        var frame = AgentSessionProtocolCodec.DeserializeFrame(
            await channel.Output.ReadAsync(TestContext.Current.CancellationToken));
        var terminal = Assert.IsType<SessionTerminalEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame));
        Assert.Equal("takeover-blocked", terminal.Reason);
        Assert.Equal("takeover-blocked", terminal.CompletionState.GetProperty("code").GetString());
        Assert.Equal("takeover", terminal.CompletionState.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task OnChannelOpenAsync_HostOpenFails_LogsFullFailureAndMatchingDiagnosticCorrelation()
    {
        using var loggerFactory = new ObservableLoggerFactory(verboseMetadataEnabled: true);
        var attempt = Guid.NewGuid().ToString("N");
        var hostFailure = new InvalidOperationException(
            "Session materialization failed for workspace planner. Authorization: Bearer secret-wire-canary",
            new IOException("runtime snapshot store offline at agent-host-7"));
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.TryGetAsync("session", 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(hostFailure);
        var provider = new Mock<ITransportPeerIdentityProvider>();
        provider.Setup(value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>())).Returns(Peer());
        await using var listener = new AgentSessionTransportListener(
            new RemoteAgentSessionHost(
                AllowingAuthorizer().Object, registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>()),
            provider.Object,
            loggerFactory.CreateLogger<AgentSessionTransportListener>());
        await using var channel = new TestChannel();

        await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(Open() with { DiagnosticAttempt = attempt }),
            channel,
            TestContext.Current.CancellationToken);

        var wireElement = await channel.Output.ReadAsync(TestContext.Current.CancellationToken);
        var wire = wireElement.GetRawText();
        var frame = AgentSessionProtocolCodec.DeserializeFrame(wireElement);
        var terminal = Assert.IsType<SessionTerminalEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame));
        var correlationId = terminal.CompletionState.GetProperty("correlation-id").GetString();
        Assert.NotNull(correlationId);

        // The wire response stays a sanitized non-dump: generic code, no raw cause or credential.
        Assert.Contains("invalid-request", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-wire-canary", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace planner", wire, StringComparison.Ordinal);

        var record = Assert.Single(
            loggerFactory.Entries, entry => entry.Contains("host-open", StringComparison.Ordinal));
        // The same validated attempt and diagnostic id join the owner log to the terminal response.
        Assert.Contains(attempt, record, StringComparison.Ordinal);
        Assert.Contains(correlationId!, record, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent-session-id session", record, StringComparison.Ordinal);
        // Full protected-aware failure detail: the ordinary message and every inner cause survive...
        Assert.Contains("Session materialization failed for workspace planner", record, StringComparison.Ordinal);
        Assert.Contains("runtime snapshot store offline at agent-host-7", record, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", record, StringComparison.Ordinal);
        // ...while the credential fragment is scrubbed before it ever reaches the sink.
        Assert.DoesNotContain("secret-wire-canary", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnChannelOpenAsync_UnauthorizedPeer_PreservesSanitizedNonDisclosure()
    {
        using var loggerFactory = new ObservableLoggerFactory(verboseMetadataEnabled: true);
        var attempt = Guid.NewGuid().ToString("N");
        var open = Open() with { DiagnosticAttempt = attempt };
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
            provider.Object,
            loggerFactory.CreateLogger<AgentSessionTransportListener>());
        await using var channel = new TestChannel();

        await listener.OnChannelOpenAsync(
            AgentSessionProtocolCodec.SerializeOpen(open), channel, TestContext.Current.CancellationToken);

        var wireElement = await channel.Output.ReadAsync(TestContext.Current.CancellationToken);
        var wire = wireElement.GetRawText();
        var frame = AgentSessionProtocolCodec.DeserializeFrame(wireElement);
        var terminal = Assert.IsType<SessionTerminalEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame));
        var correlationId = terminal.CompletionState.GetProperty("correlation-id").GetString();

        // Anti-enumeration: the unauthorized peer only sees the generic not-found denial, never the
        // session identity, private owner, or the authorization/materialization cause.
        Assert.Equal("not-found", terminal.Reason);
        Assert.DoesNotContain("agent-session-id", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(open.ExpectedOwningProfileEntityId, wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unauthorized", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authoriz", wire, StringComparison.OrdinalIgnoreCase);
        // The session lookup never runs, so existence cannot be probed through this path.
        registry.Verify(value => value.TryGetAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);

        // Correlation still works for support: the terminal id matches the local owner record, which
        // retains the real (private) identity that the wire withholds.
        Assert.NotNull(correlationId);
        var record = Assert.Single(
            loggerFactory.Entries, entry => entry.Contains(correlationId!, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("agent-session-id session", record, StringComparison.Ordinal);
        Assert.Contains(attempt, record, StringComparison.Ordinal);
    }

    private static AgentSessionTransportListener Listener(
        out Mock<IRemoteAgentSessionRuntimeRegistry> registry,
        out Mock<IAgentSessionRuntimeHostFactory> factory,
        bool authenticated = false,
        ILogger<AgentSessionTransportListener>? logger = null)
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        factory = new Mock<IAgentSessionRuntimeHostFactory>();
        var provider = new Mock<ITransportPeerIdentityProvider>();
        var identity = provider.Setup(
            value => value.GetRequiredIdentity(It.IsAny<IMessageChannel>()));
        if (authenticated)
            identity.Returns(Peer());
        else
            identity.Throws(new UnauthorizedAccessException());
        return new AgentSessionTransportListener(
            new RemoteAgentSessionHost(authorizer.Object, registry.Object, factory.Object),
            provider.Object,
            logger);
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
        chat.SetupGet(value => value.History).Returns(new AgentChatHistoryCollection());
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal LogEntry? LastLog { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.LastLog = new LogEntry(logLevel, formatter(state, exception), exception);
        }

        internal sealed record LogEntry(
            LogLevel Level,
            string Message,
            Exception? Exception);
    }
}
