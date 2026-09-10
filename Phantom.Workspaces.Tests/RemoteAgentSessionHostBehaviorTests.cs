#pragma warning disable xUnit1051
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Channels;
using AgentSchema;
using Microsoft.Extensions.AI;
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
        await using var fixture = new HostFixture();
        var childChecks = 0;
        fixture.Authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(),
                It.Is<AgentSessionAuthorizationRequest>(request =>
                    request.Operation == AgentSessionAuthorizationOperation.OpenSubagent
                    && request.ChildAgentId == "child"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AgentSessionAuthorizationDecision
            {
                IsAllowed = ++childChecks == 1,
            });
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        var command = new OpenSubagentCommand
        {
            CommandId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = fixture.Runtime.Epoch,
            AgentId = "child",
        };
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment, command);
        var first = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
            AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync()));
        Assert.IsType<CommandCompletedEvent>(first);
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment,
            command with { CommandId = Guid.NewGuid() });
        var denied = Assert.IsType<OperationErrorEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
                AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync())));
        Assert.Equal("unauthorized", denied.Error.Code);
        Assert.Equal(2, childChecks);
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
    {
        await using var fixture = new HostFixture();
        fixture.Queues.Setup(value => value.CreateQueueAsync(
                It.IsAny<CreateAgentInputQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateAgentInputQueueRequest request, CancellationToken _) =>
                new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Conflict,
                    Revision = 2,
                    ErrorCode = "stale-revision",
                });
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment,
            Command(fixture.Runtime.Epoch));
        var result = AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync());
        Assert.Equal("conflict", Assert.IsType<OperationErrorEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(result)).Error.Code);
        fixture.Queues.Verify(value => value.CreateQueueAsync(
            It.Is<CreateAgentInputQueueRequest>(request => request.ExpectedRevision == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueCommand_InvalidQueueOrItem_ReturnsRejectedWithoutDelta()
    {
        await using var fixture = new HostFixture();
        fixture.Queues.Setup(value => value.CreateQueueAsync(
                It.IsAny<CreateAgentInputQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateAgentInputQueueRequest request, CancellationToken _) =>
                new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Rejected,
                    Revision = 1,
                    ErrorCode = "invalid-queue",
                });
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        var before = fixture.Runtime.Replay.HighWaterMark;
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment,
            Command(fixture.Runtime.Epoch));
        var result = AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync());
        Assert.Equal("rejected", Assert.IsType<OperationErrorEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(result)).Error.Code);
        Assert.Equal(before + 1, fixture.Runtime.Replay.HighWaterMark);
    }

    [Fact]
    public async Task SetToolEnabledCommand_EachMutation_ReauthorizesAndBroadcastsToolsEvent()
    {
        await using var fixture = new HostFixture();
        fixture.Chat.Setup(value => value.SetToolEnabledAsync("tool", true, It.IsAny<CancellationToken>()))
            .Callback(() => fixture.Chat.Raise(value => value.ToolsChanged += null, EventArgs.Empty))
            .Returns(Task.CompletedTask);
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment,
            new SetToolEnabledCommand
        {
            CommandId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = fixture.Runtime.Epoch,
            ToolId = "tool",
            Enabled = true,
        });
        var frames = new[]
        {
            AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync()),
            AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync()),
        };
        Assert.Contains(frames, frame =>
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame) is ToolsChangedEvent);
        fixture.Authorizer.Verify(value => value.AuthorizeAsync(
            It.IsAny<TransportPeerIdentity>(),
            It.Is<AgentSessionAuthorizationRequest>(request =>
                request.Operation == AgentSessionAuthorizationOperation.SetToolState),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueCommand_Applied_BroadcastsOneOrderedDeltaToEveryViewer()
    {
        await using var fixture = new HostFixture();
        var secondChannel = new DuplexChannel();
        SetupAppliedQueue(fixture, 4);
        await using var first = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, token: "a"));
        await using var second = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, secondChannel, "b"));
        await fixture.Channel.Output.ReadAsync();
        await fixture.Channel.Output.ReadAsync();
        await secondChannel.Output.ReadAsync();
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, first,
            Command(fixture.Runtime.Epoch));
        var firstDelta = AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync());
        var secondDelta = AgentSessionProtocolCodec.DeserializeFrame(await secondChannel.Output.ReadAsync());
        Assert.Equal(firstDelta.Sequence, secondDelta.Sequence);
        Assert.Equal(4, Assert.IsType<QueueChangedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(firstDelta)).Revision);
        await secondChannel.DisposeAsync();
    }

    [Fact]
    public async Task QueueCommand_Applied_TaskCompletesAfterAuthoritativeRevisionApplied()
    {
        await using var fixture = new HostFixture();
        SetupAppliedQueue(fixture, 3);
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        await fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment,
            Command(fixture.Runtime.Epoch));
        var delta = AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync());
        var completed = AgentSessionProtocolCodec.DeserializeFrame(await fixture.Channel.Output.ReadAsync());
        Assert.IsType<QueueChangedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(delta));
        Assert.IsType<CommandCompletedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(completed));
        Assert.True(delta.Sequence < completed.Sequence);
    }

    [Fact]
    public async Task QueueConsumption_CurrentRun_BroadcastsOwnerRevisionAfterEnqueue()
    {
        var manager = new AgentInputQueueManager();
        var queue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = 1,
            Immediacy = AgentInputQueueImmediacy.Queue,
        });
        manager.RegisterInputQueue(queue);
        using var queues = new LocalAgentInputQueuesAdapter(manager, queue);
        var chat = Chat(queues);
        await using var runtime = new RemoteAgentSessionLease(
            "session", 1, new RuntimeEpoch { Value = Guid.NewGuid() }, chat.Object, true, Snapshot);
        var channel = new DuplexChannel();
        await using var attachment = runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "owner",
            Channel = channel,
        });
        var host = new RemoteAgentSessionHost(
            Allow(), Mock.Of<IRemoteAgentSessionRuntimeRegistry>(), Mock.Of<IAgentSessionRuntimeHostFactory>());
        var enqueueRevision = queues.Snapshot.Revision;
        var command = new EnqueueInputCommand
        {
            CommandId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = runtime.Epoch,
            ExpectedRevision = enqueueRevision,
            TargetQueueId = queue.QueueId,
            Messages = JsonSerializer.SerializeToElement(new[] { new ChatMessage(ChatRole.User, "run") }),
        };

        await host.DispatchCommandAsync(Peer(), Open(AgentSessionOpenIntent.Attach), runtime, attachment, command);
        var enqueued = Assert.IsType<QueueChangedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
                AgentSessionProtocolCodec.DeserializeFrame(await channel.Output.ReadAsync())));
        Assert.Equal(enqueueRevision + 1, enqueued.Revision);
        Assert.IsType<CommandCompletedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
                AgentSessionProtocolCodec.DeserializeFrame(await channel.Output.ReadAsync())));

        Assert.True(manager.TryDequeueNextImmediateOrQueued(out var consumed));
        var consumedDelta = Assert.IsType<QueueChangedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
                AgentSessionProtocolCodec.DeserializeFrame(await channel.Output.ReadAsync())));
        Assert.Equal("run", consumed.Messages!.Single().Text);
        Assert.Equal(enqueued.Revision + 1, consumedDelta.Revision);
        Assert.Empty(consumedDelta.Queues.Single(value => value.QueueId == queue.QueueId).Items);
    }

    [Fact]
    public async Task Command_EachMutation_ReauthorizesPeer()
    {
        await using var fixture = new HostFixture();
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        AgentSessionCommand[] commands =
        [
            Command(fixture.Runtime.Epoch),
            new SetToolEnabledCommand
            {
                CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
                RuntimeEpoch = fixture.Runtime.Epoch, ToolId = "tool", Enabled = true,
            },
            new InterruptCommand
            {
                CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
                RuntimeEpoch = fixture.Runtime.Epoch,
            },
        ];
        foreach (var command in commands)
            await fixture.Host.DispatchCommandAsync(
                Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment, command);
        foreach (var operation in new[]
                 {
                     AgentSessionAuthorizationOperation.Send,
                     AgentSessionAuthorizationOperation.SetToolState,
                     AgentSessionAuthorizationOperation.Interrupt,
                 })
        {
            fixture.Authorizer.Verify(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(),
                It.Is<AgentSessionAuthorizationRequest>(request => request.Operation == operation),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task TakeOverAsync_ConfirmedOldTermination_AdvancesGenerationThenStartsNewEpoch()
    {
        await using var fixture = new HostFixture();
        var replacement = Runtime();
        var takeover = Takeover();
        fixture.Factory.Setup(value => value.TryTakeOverAsync(
            It.IsAny<AgentSessionTakeoverRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        fixture.Factory.Setup(value => value.LoadIntentAsync("session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent() with
            {
                OwningProfileEntityId = takeover.NewOwningProfileEntityId,
                OwnershipGeneration = 2,
            });
        fixture.Factory.Setup(value => value.StartAsync(
                It.Is<PersistedAgentSessionRuntimeIntent>(intent => intent.OwnershipGeneration == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(replacement);
        await fixture.Host.TakeOverAsync(Peer(), takeover);
        Assert.True(fixture.Runtime.IsFenced);
        Assert.Same(replacement, await fixture.Registry.TryGetAsync("session", 2));
        fixture.Factory.Verify(value => value.TryTakeOverAsync(
            It.Is<AgentSessionTakeoverRequest>(request => request.ExpectedOwnershipGeneration == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Factory.Verify(value => value.StartAsync(
            It.Is<PersistedAgentSessionRuntimeIntent>(intent =>
                intent.OwnershipGeneration == 2
                && intent.OwningProfileEntityId == takeover.NewOwningProfileEntityId),
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
    public async Task TakeOverAsync_WaitsForOldTerminalPersistenceBeforeOwnershipExchange()
    {
        var persistenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPersistence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldRuntime = Runtime(persistTerminalAsync: async _ =>
        {
            persistenceStarted.SetResult();
            await allowPersistence.Task;
        });
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(oldRuntime));
        var factory = new Mock<IAgentSessionRuntimeHostFactory>();
        var replacement = Runtime();
        var request = Takeover();
        factory.Setup(value => value.TryTakeOverAsync(
                request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        factory.Setup(value => value.LoadIntentAsync(
                request.AgentSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent() with
            {
                OwningProfileEntityId = request.NewOwningProfileEntityId,
                OwnershipGeneration = 2,
            });
        factory.Setup(value => value.StartAsync(
                It.IsAny<PersistedAgentSessionRuntimeIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(replacement);
        var host = new RemoteAgentSessionHost(Allow(), registry, factory.Object);

        var takeover = host.TakeOverAsync(Peer(), request);
        await persistenceStarted.Task;

        factory.Verify(value => value.TryTakeOverAsync(
            It.IsAny<AgentSessionTakeoverRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        allowPersistence.SetResult();
        await takeover;

        factory.Verify(value => value.TryTakeOverAsync(
            request, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Same(replacement, await registry.TryGetAsync("session", 2));
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
        var attachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAttach = new ManualResetEventSlim();
        var snapshots = 0;
        AgentSessionSnapshot BlockingSnapshot()
        {
            if (Interlocked.Increment(ref snapshots) == 1)
            {
                attachEntered.SetResult();
                releaseAttach.Wait();
            }
            return Snapshot();
        }
        await using var attachWinningRuntime = Runtime(background: false, snapshotFactory: BlockingSnapshot);
        var originalEpoch = attachWinningRuntime.Epoch;
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(attachWinningRuntime));
        var attachWinningFactory = new Mock<IAgentSessionRuntimeHostFactory>();
        attachWinningFactory.Setup(value => value.LoadIntentAsync("session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Intent());
        var attachWinningHost = new RemoteAgentSessionHost(
            Allow(), registry, attachWinningFactory.Object);
        var first = attachWinningRuntime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "first",
            Channel = new DuplexChannel(),
        });
        var winningAttachTask = Task.Run(() => attachWinningHost.OpenAsync(new OpenAgentSessionHostRequest
        {
            Peer = Peer(),
            OpenRequest = Open(AgentSessionOpenIntent.StartOrAttach) with { AttachmentToken = "winner" },
            Channel = new DuplexChannel(),
        }));
        var attachStarted = await Task.WhenAny(attachEntered.Task, winningAttachTask);
        if (ReferenceEquals(attachStarted, winningAttachTask))
            await winningAttachTask;
        var losingStopTask = Task.Run(async () => await first.DisposeAsync());
        releaseAttach.Set();
        await using var winningAttach = await winningAttachTask;
        await losingStopTask;
        Assert.False(attachWinningRuntime.IsFenced);
        Assert.Equal(originalEpoch, attachWinningRuntime.Epoch);
        Assert.Equal(1, attachWinningRuntime.ViewerCount);
    }

    [Fact]
    public async Task RuntimeRegistry_LastViewerStopWins_WaitsForCleanupBeforeStartingFreshEpoch()
    {
        var terminalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalPersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stopWinningRuntime = Runtime(
            background: false,
            persistTerminalAsync: async _ =>
            {
                terminalEntered.SetResult();
                await releaseTerminal.Task;
                terminalPersisted.SetResult();
            });
        await using var stopWinningRegistry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await stopWinningRegistry.GetOrStartAsync(Intent(), _ => Task.FromResult(stopWinningRuntime));
        stopWinningRuntime.Terminated += (_, _) => terminated.SetResult();
        var lastViewer = stopWinningRuntime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "last",
            Channel = new DuplexChannel(),
        });
        var stopTask = lastViewer.DisposeAsync().AsTask();
        await terminalEntered.Task;

        await using var fresh = Runtime(background: false);
        var freshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementTask = stopWinningRegistry.GetOrStartAsync(
            Intent(),
            _ =>
            {
                freshStarted.SetResult();
                return Task.FromResult(fresh);
            }).AsTask();
        var replacementCompletedBeforeCleanup = replacementTask.IsCompleted;
        var freshStartedBeforeCleanup = freshStarted.Task.IsCompleted;

        releaseTerminal.SetResult();
        await stopTask;
        await terminated.Task;
        await freshStarted.Task;
        Assert.False(replacementCompletedBeforeCleanup);
        Assert.False(freshStartedBeforeCleanup);
        Assert.True(terminalPersisted.Task.IsCompleted);
        Assert.Same(fresh, await replacementTask);
        await using var freshAttachment = fresh.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "fresh",
            Channel = new DuplexChannel(),
        });
        Assert.Equal(1, fresh.ViewerCount);
        Assert.NotEqual(stopWinningRuntime.Epoch, fresh.Epoch);
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
        var released = attachment.Released;
        time.Advance(TimeSpan.FromSeconds(5));
        await released;
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

    [Fact]
    public async Task OwnerStreamingLifecycle_BroadcastsStartedUpdatedAndCompleted()
    {
        await using var fixture = new HostFixture();
        await using var attachment = await fixture.Host.OpenAsync(fixture.Request(AgentSessionOpenIntent.Attach));
        await fixture.Channel.Output.ReadAsync();
        var running = new AgentChatRunningItem();
        fixture.RunningItems.Add(running);
        running.Items.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant });
        fixture.RunningItems.Remove(running);

        var types = new List<string>();
        for (var index = 0; index < 5; index++)
            types.Add(AgentSessionProtocolCodec.DeserializeFrame(
                await fixture.Channel.Output.ReadAsync()).Type);
        Assert.Contains("streaming-started", types);
        Assert.Contains("streaming-updated", types);
        Assert.Contains("streaming-completed", types);
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

    private static void SetupAppliedQueue(HostFixture fixture, long revision)
    {
        fixture.Queues.SetupGet(value => value.Snapshot).Returns(() =>
            new AgentInputQueuesSnapshot { Revision = revision, Queues = [] });
        fixture.Queues.Setup(value => value.CreateQueueAsync(
                It.IsAny<CreateAgentInputQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateAgentInputQueueRequest request, CancellationToken _) =>
            {
                fixture.Queues.Raise(value => value.Changed += null, EventArgs.Empty);
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    Revision = revision,
                    QueueId = "queue",
                };
            });
    }

    private const string Owner = "22222222-2222-2222-2222-222222222222";

    private static CreateQueueCommand Command(RuntimeEpoch? epoch = null) => new()
    {
        CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
        RuntimeEpoch = epoch ?? new RuntimeEpoch { Value = Guid.NewGuid() }, ExpectedRevision = 1,
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

    private static RemoteAgentSessionLease Runtime(
        bool background = true,
        TimeProvider? time = null,
        Func<AgentSessionSnapshot>? snapshotFactory = null,
        Func<CancellationToken, ValueTask>? persistTerminalAsync = null)
    {
        var queues = new Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot { Revision = 0, Queues = [] });
        return new RemoteAgentSessionLease("session", 1, new RuntimeEpoch { Value = Guid.NewGuid() },
            Chat(queues).Object, background, snapshotFactory ?? Snapshot,
            persistTerminalAsync: persistTerminalAsync, timeProvider: time);
    }

    private static Mock<IAgentChat> Chat(
        Mock<IAgentInputQueues> queues,
        AgentChatRunningItemCollection? runningItems = null)
        => Chat(queues.Object, runningItems);

    private static Mock<IAgentChat> Chat(
        IAgentInputQueues queues,
        AgentChatRunningItemCollection? runningItems = null)
    {
        var chat = new Mock<IAgentChat>();
        chat.SetupGet(value => value.InputQueues).Returns(queues);
        chat.SetupGet(value => value.RunningItems).Returns(runningItems ?? new AgentChatRunningItemCollection());
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
        internal readonly Mock<IAgentSessionAttachAuthorizer> Authorizer = new();
        internal readonly AgentChatRunningItemCollection RunningItems = new();
        internal readonly DuplexChannel Channel = new();
        internal readonly RemoteAgentSessionLease Runtime;
        internal readonly RemoteAgentSessionHost Host;
        internal int FactoryStarts;

        internal HostFixture(bool started = true)
        {
            this.Queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot { Revision = 0, Queues = [] });
            this.Chat = RemoteAgentSessionHostTests.Chat(this.Queues, this.RunningItems);
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
            this.Authorizer.Setup(value => value.AuthorizeAsync(
                    It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = true });
            this.Host = new RemoteAgentSessionHost(this.Authorizer.Object, this.Registry, this.Factory.Object);
            if (started) _ = this.StartRuntimeAsync();
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
        internal ValueTask SendAsync(AgentSessionCommand command)
            => this.input.Writer.WriteAsync(AgentSessionProtocolCodec.SerializeCommand(command));
        public ValueTask DisposeAsync()
        {
            this.input.Writer.TryComplete();
            this.output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
