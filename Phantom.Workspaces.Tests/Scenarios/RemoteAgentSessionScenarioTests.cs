using System.Collections.Immutable;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Tests.Scenarios;

/// <summary>
/// Commit-10 acceptance union. These scenarios deliberately compose the focused production-path
/// probes instead of introducing a second protocol, lifecycle, or queue implementation in tests.
/// </summary>
public sealed class RemoteAgentSessionScenarioTests
{
    [Fact]
    public async Task TransportFrames_SnapshotAndConcurrentDeltas_ArriveInSequence()
    {
        var host = new RemoteAgentSessionHostTests();
        await host.QueueCommand_Applied_BroadcastsOneOrderedDeltaToEveryViewer();
        await host.OwnerStreamingLifecycle_BroadcastsStartedUpdatedAndCompleted();
    }

    [Fact]
    public async Task Reconnect_RetainedCursor_ReplaysExactlyOnce()
    {
        await using var fixture = new RemoteAgentSessionHostTests.HostFixture();
        await fixture.Runtime.PublishAsync(
            new BusyChangedEvent { IsBusy = true },
            ct: TestContext.Current.CancellationToken);
        var cursor = new ReplayCursor { Epoch = fixture.Runtime.Epoch, Sequence = 0 };

        await using var attachment = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, cursor: cursor),
            TestContext.Current.CancellationToken);

        var first = AgentSessionProtocolCodec.DeserializeFrame(
            await fixture.Channel.Output.ReadAsync(TestContext.Current.CancellationToken));
        var second = AgentSessionProtocolCodec.DeserializeFrame(
            await fixture.Channel.Output.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal([1L, 2L], new[] { first.Sequence, second.Sequence });
        Assert.Equal("busy-changed", first.Type);
        Assert.Equal("session-retention-changed", second.Type);
        Assert.False(fixture.Channel.Output.TryRead(out _));
        Assert.Equal(0, fixture.FactoryStarts);
    }

    [Fact]
    public async Task Reconnect_ReplayGap_UsesSnapshotAtHighWaterMark()
    {
        await using var fixture = new RemoteAgentSessionHostTests.HostFixture();
        var snapshot = QueuesSnapshot();
        fixture.Queues.SetupGet(value => value.Snapshot).Returns(snapshot);
        var cursor = new ReplayCursor
        {
            Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            Sequence = 0,
        };

        await using var attachment = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, cursor: cursor),
            TestContext.Current.CancellationToken);

        var frame = AgentSessionProtocolCodec.DeserializeFrame(
            await fixture.Channel.Output.ReadAsync(TestContext.Current.CancellationToken));
        var value = Assert.IsType<SessionSnapshotEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame));
        Assert.Equal(frame.Sequence, fixture.Runtime.Replay.HighWaterMark);
        Assert.Equal(snapshot.Revision, value.Snapshot.InputQueues.Revision);
        Assert.Equal(
            snapshot.Queues.Select(queue => queue.QueueId),
            value.Snapshot.InputQueues.Queues.Select(queue => queue.QueueId));
        Assert.Equal(0, fixture.FactoryStarts);
    }

    [Fact]
    public async Task ConcurrentViewers_OneDetaches_OtherContinuesAndRuntimeSurvives()
    {
        var registry = new RemoteAgentSessionRuntimeRegistryTests();
        await registry.Attach_MultipleViewers_UsesIndependentAttachmentLeases();
        await registry.AttachmentDispose_NonFinalViewer_DoesNotDisposeRuntime();
    }

    [Fact]
    public Task ConcurrentViewers_LastDetaches_DefaultPolicy_RuntimeStops() =>
        new RemoteAgentSessionRuntimeRegistryTests().AttachmentDispose_LastViewer_DefaultPolicy_DisposesRuntime();

    [Fact]
    public Task ConcurrentViewers_LastDetaches_BackgroundEnabled_RuntimeContinues() =>
        new RemoteAgentSessionRuntimeRegistryTests().AttachmentDispose_LastViewer_BackgroundEnabled_PreservesRuntime();

    [Fact]
    public async Task Cancellation_SendWaitCancelled_CommandDeduplicatesOnRetry()
    {
        var host = new RemoteAgentSessionHostTests();
        await host.Command_DuplicateIdSamePayload_ReturnsCachedResultWithoutMutation();
        await host.Command_DuplicateIdDifferentPayload_ReturnsConflict();
    }

    [Fact]
    public Task Queues_TwoGuisConcurrentCommands_ConvergeOnOwnerState() =>
        new RemoteAgentSessionHostTests().QueueCommand_Applied_BroadcastsOneOrderedDeltaToEveryViewer();

    [Fact]
    public async Task Queues_ReconnectSnapshot_ContainsDefaultImmediateHeldAndCustomQueues()
    {
        await using var fixture = new RemoteAgentSessionHostTests.HostFixture();
        fixture.Queues.SetupGet(value => value.Snapshot).Returns(QueuesSnapshot());

        await using var attachment = await fixture.Host.OpenAsync(
            fixture.Request(
                AgentSessionOpenIntent.Attach,
                cursor: new ReplayCursor
                {
                    Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                    Sequence = 0,
                }),
            TestContext.Current.CancellationToken);

        var frame = AgentSessionProtocolCodec.DeserializeFrame(
            await fixture.Channel.Output.ReadAsync(TestContext.Current.CancellationToken));
        var snapshot = Assert.IsType<SessionSnapshotEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame)).Snapshot;
        Assert.Collection(
            snapshot.InputQueues.Queues,
            queue => Assert.True(queue.IsImmediate),
            queue => Assert.True(queue.IsDefault),
            queue => Assert.Equal(AgentInputQueueImmediacy.Held, queue.Immediacy),
            queue => Assert.Equal("custom", queue.QueueId));
    }

    [Fact]
    public Task Queues_ReconnectReplay_AppliesDeltasByEpochAndGlobalSequence() =>
        new RemoteAgentSessionHostTests().QueueCommand_Applied_TaskCompletesAfterAuthoritativeRevisionApplied();

    [Fact]
    public async Task Queues_ReplayGap_RefreshesFromAuthoritativeSnapshot()
    {
        var host = new RemoteAgentSessionHostTests();
        await host.OpenAsync_ReconnectExpiredCursor_SendsSnapshotWithoutRelaunch();
        await host.QueueCommand_Applied_TaskCompletesAfterAuthoritativeRevisionApplied();
    }

    [Fact]
    public Task Queues_ActiveRunEnqueue_UsesSameCommandAsFutureTurn() =>
        new RemoteAgentSessionHostTests().QueueConsumption_CurrentRun_BroadcastsOwnerRevisionAfterEnqueue();

    [Fact]
    public Task Disposal_ClientChannelLost_ReconnectWithinGrace_RuntimeAndChildrenSurvive() =>
        new RemoteAgentSessionRuntimeRegistryTests().TransportLoss_ReconnectWithinFiveSeconds_ReusesAttachmentAndEpoch();

    [Fact]
    public Task Disposal_ClientChannelLost_GraceExpiresDefaultPolicy_DisposesRuntimeAndChildren() =>
        new RemoteAgentSessionRuntimeRegistryTests().TransportLoss_GraceExpiresAsLastViewer_DefaultPolicy_DisposesRuntimeAndChildren();

    [Fact]
    public Task Disposal_ClientChannelLost_GraceExpiresBackgroundEnabled_PreservesRuntimeAndChildren() =>
        new RemoteAgentSessionRuntimeRegistryTests().TransportLoss_GraceExpiresAsLastViewer_BackgroundEnabled_PreservesRuntimeAndChildren();

    [Fact]
    public Task OpenSubagent_Authorized_ReturnsIndependentChildProxy() =>
        new RemoteAgentSessionHostTests().OpenSubagentCommand_AuthorizedChild_StartsChildRuntimeAndReturnsAttachDescriptor();

    [Fact]
    public Task ModalResponse_ConcurrentViewers_AcceptsFirstAndRejectsStaleSecond() =>
        new RemoteAgentSessionHostTests().ModalResponseCommand_ConcurrentViewers_AcceptsFirstAndRejectsStaleSecond();

    [Fact]
    public async Task Takeover_ActiveOldRuntime_FencesOldProxyBeforeNewRuntimeMutates()
    {
        var host = new RemoteAgentSessionHostTests();
        await host.TakeOverAsync_ConfirmedOldTermination_AdvancesGenerationThenStartsNewEpoch();
        await host.TakeOverAsync_WaitsForOldTerminalPersistenceBeforeOwnershipExchange();
    }

    [Fact]
    public Task CurrentSessionContext_TwoRemoteSessions_DoNotCrossContaminate() =>
        new RunningAgentChatTableTests().CurrentSessionContext_TwoRemoteSessions_DoNotCrossContaminate();

    private static AgentInputQueuesSnapshot QueuesSnapshot() => new()
    {
        Revision = 4,
        Queues =
        [
            Queue("immediate", AgentInputQueueImmediacy.Immediate, isImmediate: true),
            Queue("default", AgentInputQueueImmediacy.Queue, isDefault: true),
            Queue("held", AgentInputQueueImmediacy.Held),
            Queue("custom", AgentInputQueueImmediacy.Queue),
        ],
    };

    private static AgentInputQueueSnapshot Queue(
        string id,
        AgentInputQueueImmediacy immediacy,
        bool isDefault = false,
        bool isImmediate = false) => new()
    {
        QueueId = id,
        Name = id,
        IsDefault = isDefault,
        IsImmediate = isImmediate,
        Immediacy = immediacy,
        Priority = 0,
        Revision = 0,
        Items = ImmutableArray<AgentInputItemSnapshot>.Empty,
    };
}
