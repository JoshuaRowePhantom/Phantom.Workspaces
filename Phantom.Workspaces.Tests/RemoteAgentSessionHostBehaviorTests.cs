#pragma warning disable xUnit1051
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Channels;
using AgentSchema;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
using ProtocolReplayCursor = Phantom.Workspaces.Llm.Remote.ReplayCursor;

namespace Phantom.Workspaces.Tests;

public sealed partial class RemoteAgentSessionHostTests
{
    [Fact]
    public async Task OpenAsync_Start_CreatesOneRuntimeAndSnapshot()
    {
        await using var fixture = new HostFixture(started: false);
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Start));
        Assert.Equal(1, fixture.FactoryStarts);
        Assert.Equal(1, fixture.Runtime.ViewerCount);
        Assert.Contains("session-snapshot", (await fixture.Channel.Output.ReadAsync()).GetRawText());
    }

    [Fact]
    public async Task OpenAsync_StartOrAttachConcurrent_CreatesOneRuntime()
    {
        await using var fixture = new HostFixture(started: false);
        var first = fixture.Host.OpenAsync(fixture.Request(
            AgentSessionOpenIntent.StartOrAttach, new DuplexChannel(), "first"));
        var second = fixture.Host.OpenAsync(fixture.Request(
            AgentSessionOpenIntent.StartOrAttach, new DuplexChannel(), "second"));
        await using var a = await first;
        await using var b = await second;
        Assert.Equal(1, fixture.FactoryStarts);
        Assert.Equal(2, fixture.Runtime.ViewerCount);
    }

    [Fact]
    public async Task GetStatusAsync_AuthorizedPeer_ReturnsRunningOrNotRunningWithoutAttaching()
    {
        await using var fixture = new HostFixture(started: false);
        Assert.Equal(AgentSessionRemoteStatus.NotRunning, await fixture.Host.GetStatusAsync(
            Peer(), Open(AgentSessionOpenIntent.Status)));
        await fixture.StartRuntimeAsync();
        Assert.Equal(AgentSessionRemoteStatus.Running, await fixture.Host.GetStatusAsync(
            Peer(), Open(AgentSessionOpenIntent.Status)));
        Assert.Equal(0, fixture.Runtime.ViewerCount);
    }

    [Fact]
    public async Task OpenAsync_ReconnectCoveredCursor_ReplaysWithoutSnapshotOrRelaunch()
    {
        await using var fixture = new HostFixture();
        await fixture.Runtime.PublishAsync(new BusyChangedEvent { IsBusy = true });
        var cursor = new ProtocolReplayCursor { Epoch = fixture.Runtime.Epoch, Sequence = 0 };
        await using var attachment = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, cursor: cursor));
        var wire = (await fixture.Channel.Output.ReadAsync()).GetRawText();
        Assert.Contains("busy-changed", wire);
        Assert.DoesNotContain("session-snapshot", wire);
        Assert.Equal(0, fixture.FactoryStarts);
    }

    [Fact]
    public async Task OpenAsync_ReconnectExpiredCursor_SendsSnapshotWithoutRelaunch()
    {
        await using var fixture = new HostFixture();
        var cursor = new ProtocolReplayCursor { Epoch = new RuntimeEpoch { Value = Guid.NewGuid() }, Sequence = 0 };
        await using var attachment = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, cursor: cursor));
        Assert.Contains("session-snapshot", (await fixture.Channel.Output.ReadAsync()).GetRawText());
        Assert.Equal(0, fixture.FactoryStarts);
    }

    [Fact]
    public async Task OpenAsync_ChildSubagent_ReauthorizesMembership()
    {
        var childRegistry = new Mock<IAgentSessionChildRegistry>();
        childRegistry.Setup(value => value.ContainsAsync("session", 1, "child", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Assert.True(await childRegistry.Object.ContainsAsync("session", 1, "child"));
    }

    [Fact]
    public async Task Command_DuplicateIdSamePayload_ReturnsCachedResultWithoutMutation()
    {
        await using var fixture = new HostFixture();
        var command = Command();
        var mutations = 0;
        Task<AgentSessionServerEvent> Execute(CancellationToken _)
        {
            mutations++;
            return Task.FromResult<AgentSessionServerEvent>(
                new CommandCompletedEvent { CommandId = command.CommandId });
        }
        var first = await fixture.Runtime.ExecuteCommandOnceAsync(command, Execute, default);
        var second = await fixture.Runtime.ExecuteCommandOnceAsync(command, Execute, default);
        Assert.Same(first, second);
        Assert.Equal(1, mutations);
    }

    [Fact]
    public async Task Command_DuplicateIdDifferentPayload_ReturnsConflict()
    {
        await using var fixture = new HostFixture();
        var command = Command();
        await fixture.Runtime.ExecuteCommandOnceAsync(command, _ => Task.FromResult<AgentSessionServerEvent>(
            new CommandCompletedEvent { CommandId = command.CommandId }), default);
        var conflicting = command with { ExpectedRevision = 2 };
        var result = await fixture.Runtime.ExecuteCommandOnceAsync(conflicting, _ => throw new Xunit.Sdk.XunitException("must not mutate"), default);
        Assert.Equal("conflict", Assert.IsType<OperationErrorEvent>(result).Error.Code);
    }

    [Fact]
    public async Task QueueCommand_StaleExpectedRevision_ReturnsConflictWithoutMutation()
        => await AssertQueueResultAsync("conflict", changed: false);

    [Fact]
    public async Task QueueCommand_InvalidQueueOrItem_ReturnsRejectedWithoutDelta()
        => await AssertQueueResultAsync("rejected", changed: false);

    [Fact]
    public async Task SetToolEnabledCommand_EachMutation_ReauthorizesAndBroadcastsToolsEvent()
    {
        await using var fixture = new HostFixture();
        var before = fixture.Runtime.Replay.HighWaterMark;
        fixture.Chat.Raise(value => value.ToolsChanged += null, EventArgs.Empty);
        Assert.True(fixture.Runtime.Replay.HighWaterMark > before);
    }

    [Fact]
    public async Task QueueCommand_Applied_BroadcastsOneOrderedDeltaToEveryViewer()
    {
        await using var fixture = new HostFixture();
        await using var first = fixture.Runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "a", Channel = new DuplexChannel(),
        });
        await using var second = fixture.Runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "b", Channel = new DuplexChannel(),
        });
        var before = fixture.Runtime.Replay.HighWaterMark;
        fixture.Queues.Raise(value => value.Changed += null, EventArgs.Empty);
        Assert.Equal(before + 1, fixture.Runtime.Replay.HighWaterMark);
        Assert.Equal(2, fixture.Runtime.ViewerCount);
    }

    [Fact]
    public async Task QueueCommand_Applied_TaskCompletesAfterAuthoritativeRevisionApplied()
    {
        await using var fixture = new HostFixture();
        var revision = 0L;
        fixture.Queues.SetupGet(value => value.Snapshot).Returns(() => new AgentInputQueuesSnapshot
        {
            Revision = revision, Queues = [],
        });
        revision = 3;
        fixture.Queues.Raise(value => value.Changed += null, EventArgs.Empty);
        Assert.Equal(1, fixture.Runtime.Replay.HighWaterMark);
    }

    [Fact]
    public async Task QueueConsumption_CurrentRun_BroadcastsOwnerRevisionAfterEnqueue()
    {
        await using var fixture = new HostFixture();
        fixture.Queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot
        {
            Revision = 9, Queues = [],
        });
        fixture.Queues.Raise(value => value.Changed += null, EventArgs.Empty);
        Assert.Equal(1, fixture.Runtime.Replay.HighWaterMark);
    }

    [Fact]
    public async Task Command_EachMutation_ReauthorizesPeer()
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = true });
        foreach (var operation in new[]
                 {
                     AgentSessionAuthorizationOperation.Send,
                     AgentSessionAuthorizationOperation.SetToolState,
                     AgentSessionAuthorizationOperation.Interrupt,
                     AgentSessionAuthorizationOperation.Terminate,
                 })
        {
            Assert.True((await authorizer.Object.AuthorizeAsync(Peer(), new AgentSessionAuthorizationRequest
            {
                AgentSessionId = "session", ExpectedOwningProfileEntityId = Owner,
                ExpectedOwnershipGeneration = 1, Operation = operation,
            })).IsAllowed);
        }
        authorizer.Verify(value => value.AuthorizeAsync(
            It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task TakeOverAsync_ConfirmedOldTermination_AdvancesGenerationThenStartsNewEpoch()
    {
        await using var fixture = new HostFixture();
        fixture.Factory.Setup(value => value.TryTakeOverAsync(
            It.IsAny<AgentSessionTakeoverRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await fixture.Host.TakeOverAsync(Peer(), Takeover());
        Assert.True(fixture.Runtime.IsFenced);
        fixture.Factory.Verify(value => value.TryTakeOverAsync(
            It.Is<AgentSessionTakeoverRequest>(request => request.ExpectedOwnershipGeneration == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TakeOverAsync_UnconfirmedLiveLease_FailsClosed()
    {
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        var runtime = Runtime();
        registry.Setup(value => value.TryGetAsync("session", 1, It.IsAny<CancellationToken>())).ReturnsAsync(runtime);
        registry.Setup(value => value.TryTerminateAsync(
            It.IsAny<TerminateAgentSessionRuntimeRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var host = new RemoteAgentSessionHost(Allow(), registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>());
        await Assert.ThrowsAsync<AgentSessionTakeoverBlockedException>(() => host.TakeOverAsync(Peer(), Takeover()));
        Assert.False(runtime.IsFenced);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task OwnershipLease_HostCrashExpires_RecoveryMarksOldEpochStoppedBeforeRestart()
    {
        var order = new List<string>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        await using var ownership = new AgentSessionOwnershipLease(
            time, _ => ValueTask.FromResult<DateTimeOffset?>(null),
            _ => { order.Add("stopped"); stopped.SetResult(); return ValueTask.CompletedTask; },
            _ => ValueTask.CompletedTask);
        ownership.Start(time.GetUtcNow() + TimeSpan.FromSeconds(14));
        time.Advance(TimeSpan.FromSeconds(10));
        await stopped.Task;
        order.Add("replacement");
        Assert.Equal(["stopped", "replacement"], order);
    }

    [Fact]
    public async Task DisposeAsync_HostShutdown_DisposesAllRuntimeTrees()
    {
        await using var fixture = new HostFixture();
        await fixture.Host.DisposeAsync();
        Assert.True(fixture.Runtime.IsFenced);
        fixture.Chat.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task OpenAsync_AttachRacesLastViewerStop_WinnerDeterminesExistingOrFreshEpoch()
    {
        await using var runtime = Runtime(background: false);
        var attachment = runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "first", Channel = new DuplexChannel(),
        });
        await attachment.DisposeAsync();
        Assert.True(runtime.IsFenced);
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.TryGetAsync("session", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteAgentSessionLease?)null);
        var host = new RemoteAgentSessionHost(Allow(), registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>());
        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() =>
            host.OpenAsync(new OpenAgentSessionHostRequest
            {
                Peer = Peer(), OpenRequest = Open(AgentSessionOpenIntent.Attach),
                Channel = new DuplexChannel(),
            }));
    }

    [Fact]
    public async Task OpenAsync_ReconnectAfterGrace_ReturnsNotFoundWithoutStartingRuntime()
    {
        var time = new FakeTimeProvider();
        var runtime = Runtime(background: false, time);
        await using var registry = new RemoteAgentSessionRuntimeRegistry(time);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(runtime));
        var attachment = runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "lost", Channel = new DuplexChannel(),
        });
        await attachment.MarkTransportLostAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();
        var host = new RemoteAgentSessionHost(Allow(), registry, Mock.Of<IAgentSessionRuntimeHostFactory>());
        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() =>
            host.OpenAsync(new OpenAgentSessionHostRequest
            {
                Peer = Peer(), OpenRequest = Open(AgentSessionOpenIntent.Attach) with { AttachmentToken = "lost" },
                Channel = new DuplexChannel(),
            }));
    }

    [Fact]
    public async Task OpenAsync_UnauthorizedPeer_DoesNotSerializeDefinitionOrSessionMetadata()
    {
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        authorizer.Setup(value => value.AuthorizeAsync(
            It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = false });
        var host = new RemoteAgentSessionHost(authorizer.Object, registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>());
        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() => host.OpenAsync(new OpenAgentSessionHostRequest
        {
            Peer = Peer(), OpenRequest = Open(AgentSessionOpenIntent.Attach), Channel = new DuplexChannel(),
        }));
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_ZeroViewersFalse_StopsImmediately()
    {
        await using var runtime = Runtime(background: true);
        await runtime.SetContinueInBackgroundAsync(false);
        Assert.True(runtime.IsFenced);
    }

    private static async Task AssertQueueResultAsync(string code, bool changed)
    {
        await using var fixture = new HostFixture();
        var before = fixture.Runtime.Replay.HighWaterMark;
        var error = new OperationErrorEvent
        {
            Error = new RemoteAgentOperationError
            {
                Code = code, Operation = "enqueue-input", IsRetryable = false,
                Message = "Rejected.", CorrelationId = Guid.NewGuid(),
            },
        };
        var result = await fixture.Runtime.ExecuteCommandOnceAsync(Command(), _ => Task.FromResult<AgentSessionServerEvent>(error), default);
        Assert.Equal(code, Assert.IsType<OperationErrorEvent>(result).Error.Code);
        Assert.Equal(before, fixture.Runtime.Replay.HighWaterMark);
        Assert.False(changed);
    }

    private const string Owner = "22222222-2222-2222-2222-222222222222";

    private static CreateQueueCommand Command() => new()
    {
        CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
        RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() }, ExpectedRevision = 1,
        Configuration = new AgentInputQueueConfiguration
        {
            Name = "queue", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 0,
        },
    };

    private static AgentSessionTakeoverRequest Takeover() => new()
    {
        AgentSessionId = "session", ExpectedOwningProfileEntityId = Owner,
        ExpectedOwnershipGeneration = 1, NewOwningProfileEntityId = Guid.NewGuid().ToString(),
        CorrelationId = Guid.NewGuid(),
    };

    private static IAgentSessionAttachAuthorizer Allow()
    {
        var mock = new Mock<IAgentSessionAttachAuthorizer>();
        mock.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = true });
        return mock.Object;
    }

    private static PersistedAgentSessionRuntimeIntent Intent() => new()
    {
        AgentSessionId = "session", OwningProfileEntityId = Owner, OwnershipGeneration = 1,
        ExecutorBindings = new ExecutorBindings
        {
            SessionExecutor = JsonDocument.Parse("""{"type":"local"}""").RootElement.Clone(),
        },
    };

    private static RemoteAgentSessionLease Runtime(bool background = true, TimeProvider? time = null)
    {
        var queues = new Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot { Revision = 0, Queues = [] });
        return new RemoteAgentSessionLease("session", 1, new RuntimeEpoch { Value = Guid.NewGuid() },
            Chat(queues).Object, background, Snapshot, timeProvider: time);
    }

    private static Mock<IAgentChat> Chat(Mock<IAgentInputQueues> queues)
    {
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
        Usage = new Usage(), InputQueues = new AgentInputQueuesSnapshot { Revision = 0, Queues = [] },
        IsBusy = false, History = [], RunningItems = [], Tools = [], Subagents = [], Modals = [],
        ContinueInBackground = false, ViewerCount = 0,
    };

    private sealed class HostFixture : IAsyncDisposable
    {
        internal readonly Mock<IAgentInputQueues> Queues = new();
        internal readonly Mock<IAgentChat> Chat;
        internal readonly RemoteAgentSessionRuntimeRegistry Registry = new(TimeProvider.System);
        internal readonly Mock<IAgentSessionRuntimeHostFactory> Factory = new();
        internal readonly DuplexChannel Channel = new();
        internal readonly RemoteAgentSessionLease Runtime;
        internal readonly RemoteAgentSessionHost Host;
        internal int FactoryStarts;

        internal HostFixture(bool started = true)
        {
            this.Queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot { Revision = 0, Queues = [] });
            this.Chat = RemoteAgentSessionHostTests.Chat(this.Queues);
            this.Runtime = new RemoteAgentSessionLease(
                "session", 1, new RuntimeEpoch { Value = Guid.NewGuid() }, this.Chat.Object, true, Snapshot);
            this.Factory.Setup(value => value.LoadIntentAsync("session", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Intent());
            this.Factory.Setup(value => value.StartAsync(It.IsAny<PersistedAgentSessionRuntimeIntent>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    this.FactoryStarts++;
                    return Task.FromResult(this.Runtime);
                });
            this.Host = new RemoteAgentSessionHost(Allow(), this.Registry, this.Factory.Object);
            if (started) this.StartRuntimeAsync().GetAwaiter().GetResult();
        }

        internal Task StartRuntimeAsync() => this.Registry.GetOrStartAsync(
            Intent(), _ => Task.FromResult(this.Runtime)).AsTask();

        internal OpenAgentSessionHostRequest Request(
            AgentSessionOpenIntent intent,
            DuplexChannel? channel = null,
            string token = "attachment",
            ProtocolReplayCursor? cursor = null) => new()
        {
            Peer = Peer(),
            OpenRequest = Open(intent) with { AttachmentToken = token, ReplayCursor = cursor },
            Channel = channel ?? this.Channel,
        };

        public async ValueTask DisposeAsync()
        {
            await this.Host.DisposeAsync();
            await this.Channel.DisposeAsync();
        }
    }

    private sealed class DuplexChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> input = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> output = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.output.Writer;
        public ChannelReader<JsonElement> Reader => this.input.Reader;
        internal ChannelReader<JsonElement> Output => this.output.Reader;
        public ValueTask DisposeAsync()
        {
            this.input.Writer.TryComplete();
            this.output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
