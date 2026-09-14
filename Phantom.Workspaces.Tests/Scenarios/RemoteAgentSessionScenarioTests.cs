using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json;
using System.Threading.Channels;
using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Tests.Scenarios;

public sealed class RemoteAgentSessionScenarioTests
{
    [Fact]
    public async Task TransportFrames_SnapshotAndConcurrentDeltas_ArriveInSequence()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("frames", background: true));
        await using var first = await fixture.OpenProxyAsync("frames", AgentSessionOpenIntent.Start);
        await using var second = await fixture.OpenProxyAsync("frames", AgentSessionOpenIntent.Attach);
        var firstSequences = new ConcurrentQueue<long>();
        var secondSequences = new ConcurrentQueue<long>();
        first.Client.FrameReceived += (_, frame) => firstSequences.Enqueue(frame.Sequence);
        second.Client.FrameReceived += (_, frame) => secondSequences.Enqueue(frame.Sequence);
        var firstReady = WaitForQueueRevisionAsync(first.Chat, 2);
        var secondReady = WaitForQueueRevisionAsync(second.Chat, 2);
        await first.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("delta-one", 0),
            TestContext.Current.CancellationToken);
        await second.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("delta-two", 1),
            TestContext.Current.CancellationToken);

        await Task.WhenAll(firstReady, secondReady);
        var ownerQueues = ((IAgentChat)await fixture.OwnerChatAsync("frames", 0)).InputQueues;
        AssertQueueStateEqual(first.Chat.InputQueues.Snapshot, second.Chat.InputQueues.Snapshot);
        AssertQueueStateEqual(ownerQueues.Snapshot, first.Chat.InputQueues.Snapshot);
        Assert.True(IsStrictlyIncreasing(firstSequences));
        Assert.True(IsStrictlyIncreasing(secondSequences));
        Assert.Equal(first.Client.LastAppliedCursor, second.Client.LastAppliedCursor);
    }

    [Fact]
    public async Task Reconnect_RetainedCursor_ReplaysExactlyOnce()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("replay", background: true));
        await using var proxy = await fixture.OpenProxyAsync("replay", AgentSessionOpenIntent.Start);
        var original = proxy.Client.LastAppliedCursor!.Value;
        var disconnected = WaitForDisconnectAsync(proxy.Client);

        await proxy.Transport.DropConnectionsAsync();
        await disconnected;
        var owner = await fixture.OwnerChatAsync("replay", 0);
        owner.EnqueueSystemNote("while-disconnected");
        var replayed = WaitForHistoryCountAsync(proxy.Chat, 1);

        await proxy.Chat.ReconnectNowAsync(TestContext.Current.CancellationToken);
        await replayed;

        Assert.Equal(["while-disconnected"], TextHistory(proxy.Chat));
        Assert.Equal(original.Epoch, proxy.Client.LastAppliedCursor!.Value.Epoch);
        Assert.True(proxy.Client.LastAppliedCursor.Value.Sequence > original.Sequence);
    }

    [Fact]
    public async Task Reconnect_ReplayGap_UsesSnapshotAtHighWaterMark()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("gap", background: true));
        var token = Guid.NewGuid().ToString("N");
        await using var original = await fixture.OpenProxyAsync(
            "gap", AgentSessionOpenIntent.Start, attachmentToken: token);
        var disconnected = WaitForDisconnectAsync(original.Client);
        await original.Transport.DropConnectionsAsync();
        await disconnected;
        await original.Chat.DisposeAsync();
        var owner = await fixture.OwnerChatAsync("gap", 0);
        owner.EnqueueSystemNote("authoritative-snapshot");

        await using var replacement = await fixture.OpenProxyAsync(
            "gap",
            AgentSessionOpenIntent.Attach,
            attachmentToken: token,
            cursor: new ReplayCursor
            {
                Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                Sequence = 0,
            });

        Assert.Equal(["authoritative-snapshot"], TextHistory(replacement.Chat));
        Assert.Equal(
            (await fixture.RuntimeAsync("gap", 0)).Replay.HighWaterMark,
            replacement.Client.LastAppliedCursor!.Value.Sequence);
    }

    [Fact]
    public async Task ConcurrentViewers_OneDetaches_OtherContinuesAndRuntimeSurvives()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("detach-one"));
        await using var first = await fixture.OpenProxyAsync("detach-one", AgentSessionOpenIntent.Start);
        await using var second = await fixture.OpenProxyAsync("detach-one", AgentSessionOpenIntent.Attach);
        var runtime = await fixture.RuntimeAsync("detach-one", 0);

        await first.Chat.DetachAsync(TestContext.Current.CancellationToken);
        var changed = WaitForQueueRevisionAsync(second.Chat, 1);
        var result = await second.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("survivor", second.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken);
        await changed;

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Equal(1, runtime.ViewerCount);
        Assert.False(runtime.IsFenced);
        Assert.Contains(second.Chat.InputQueues.Snapshot.Queues, queue => queue.Name == "survivor");
    }

    [Fact]
    public async Task ConcurrentViewers_LastDetaches_DefaultPolicy_RuntimeStops()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("detach-last"));
        await using var proxy = await fixture.OpenProxyAsync("detach-last", AgentSessionOpenIntent.Start);
        var runtime = await fixture.RuntimeAsync("detach-last", 0);
        var terminated = WaitForRuntimeTerminationAsync(runtime);

        await proxy.Chat.DetachAsync(TestContext.Current.CancellationToken);
        await terminated;

        Assert.True(runtime.IsFenced);
        Assert.True(runtime.HasTerminated);
        Assert.Null(await fixture.TryRuntimeAsync("detach-last", 0));
        Assert.Equal("stopped", (await fixture.ReadSessionAsync("detach-last"))
            .GetProperty("runtime-state").GetString());
    }

    [Fact]
    public async Task ConcurrentViewers_LastDetaches_BackgroundEnabled_RuntimeContinues()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("background", background: true));
        await using var proxy = await fixture.OpenProxyAsync("background", AgentSessionOpenIntent.Start);
        var runtime = await fixture.RuntimeAsync("background", 0);

        await proxy.Chat.DetachAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, runtime.ViewerCount);
        Assert.False(runtime.IsFenced);
        Assert.True((await fixture.ReadSessionAsync("background"))
            .GetProperty("continue-in-background").GetBoolean());
    }

    [Fact]
    public async Task Cancellation_SendWaitCancelled_CommandDeduplicatesOnRetry()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("cancel", background: true));
        await using var proxy = await fixture.OpenProxyAsync("cancel", AgentSessionOpenIntent.Start);
        var request = CreateQueue("only-once", 0);
        var writeGate = fixture.Listener.LastChannel.ArmNextAsyncWrite();
        using var cancellation = new CancellationTokenSource();
        var first = proxy.Chat.InputQueues.CreateQueueAsync(request, cancellation.Token);
        await writeGate.Started;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        writeGate.Release();
        await writeGate.Completed;
        var retry = await proxy.Chat.InputQueues.CreateQueueAsync(
            request, TestContext.Current.CancellationToken);
        var runtime = await fixture.RuntimeAsync("cancel", 0);

        Assert.Equal(AgentInputQueueCommandStatus.Applied, retry.Status);
        Assert.Equal(1, runtime.Chat.InputQueues.Snapshot.Revision);
        Assert.Single(runtime.Chat.InputQueues.Snapshot.Queues, queue => queue.Name == "only-once");
        Assert.Equal(2, fixture.Listener.LastChannel.ReceivedMessages.Count(
            value => value.GetRawText().Contains(request.CommandId.ToString(), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Queues_TwoGuisConcurrentCommands_ConvergeOnOwnerState()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("two-guis", background: true));
        await using var first = await fixture.OpenProxyAsync("two-guis", AgentSessionOpenIntent.Start);
        await using var second = await fixture.OpenProxyAsync("two-guis", AgentSessionOpenIntent.Attach);
        var firstCommand = first.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("first", 0), TestContext.Current.CancellationToken);
        var secondCommand = second.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("second", 0), TestContext.Current.CancellationToken);

        var firstOutcome = await ObserveAsync(firstCommand);
        var secondOutcome = await ObserveAsync(secondCommand);
        Assert.NotEqual(firstOutcome is null, secondOutcome is null);
        await Task.WhenAll(
            WaitForQueueRevisionAsync(first.Chat, 1),
            WaitForQueueRevisionAsync(second.Chat, 1));
        var retrying = firstOutcome is null ? first : second;
        var retryName = firstOutcome is null ? "first" : "second";
        var converged = Task.WhenAll(
            WaitForQueueRevisionAsync(first.Chat, 2),
            WaitForQueueRevisionAsync(second.Chat, 2));

        var retry = await retrying.Chat.InputQueues.CreateQueueAsync(
            CreateQueue(retryName, retrying.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken);
        await converged;

        Assert.Equal(AgentInputQueueCommandStatus.Applied, retry.Status);
        AssertQueueStateEqual(
            first.Chat.InputQueues.Snapshot,
            second.Chat.InputQueues.Snapshot);
        AssertQueueStateEqual(
            first.Chat.InputQueues.Snapshot,
            ((IAgentChat)(await fixture.RuntimeAsync("two-guis", 0)).Chat).InputQueues.Snapshot);
        Assert.Contains(first.Chat.InputQueues.Snapshot.Queues, queue => queue.Name == "first");
        Assert.Contains(first.Chat.InputQueues.Snapshot.Queues, queue => queue.Name == "second");
    }

    [Fact]
    public async Task Queues_ReconnectSnapshot_ContainsDefaultImmediateHeldAndCustomQueues()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("queue-snapshot", background: true));
        var token = Guid.NewGuid().ToString("N");
        await using var original = await fixture.OpenProxyAsync(
            "queue-snapshot", AgentSessionOpenIntent.Start, attachmentToken: token);
        await original.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("held", 0, AgentInputQueueImmediacy.Held),
            TestContext.Current.CancellationToken);
        await original.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("custom", 1),
            TestContext.Current.CancellationToken);
        var disconnected = WaitForDisconnectAsync(original.Client);
        await original.Transport.DropConnectionsAsync();
        await disconnected;
        await original.Chat.DisposeAsync();

        await using var replacement = await fixture.OpenProxyAsync(
            "queue-snapshot",
            AgentSessionOpenIntent.Attach,
            attachmentToken: token,
            cursor: new ReplayCursor
            {
                Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                Sequence = 0,
            });
        var queues = replacement.Chat.InputQueues.Snapshot.Queues;

        Assert.Contains(queues, queue => queue.IsImmediate);
        Assert.Contains(queues, queue => queue.IsDefault);
        Assert.Contains(queues, queue => queue.Name == "held" && queue.Immediacy == AgentInputQueueImmediacy.Held);
        Assert.Contains(queues, queue => queue.Name == "custom");
    }

    [Fact]
    public async Task Queues_ReconnectReplay_AppliesDeltasByEpochAndGlobalSequence()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("queue-replay", background: true));
        await using var proxy = await fixture.OpenProxyAsync("queue-replay", AgentSessionOpenIntent.Start);
        var before = proxy.Client.LastAppliedCursor!.Value;
        var disconnected = WaitForDisconnectAsync(proxy.Client);
        await proxy.Transport.DropConnectionsAsync();
        await disconnected;
        var owner = await fixture.OwnerChatAsync("queue-replay", 0);
        var ownerQueues = ((IAgentChat)owner).InputQueues;
        ownerQueues.CreateQueue(CreateQueue("replayed-one", 0));
        ownerQueues.CreateQueue(CreateQueue("replayed-two", 1));
        var applied = WaitForQueueRevisionAsync(proxy.Chat, 2);

        await proxy.Chat.ReconnectNowAsync(TestContext.Current.CancellationToken);
        await applied;

        Assert.Equal(before.Epoch, proxy.Client.LastAppliedCursor!.Value.Epoch);
        Assert.True(proxy.Client.LastAppliedCursor.Value.Sequence >= before.Sequence + 2);
        AssertQueueStateEqual(ownerQueues.Snapshot, proxy.Chat.InputQueues.Snapshot);
    }

    [Fact]
    public async Task Queues_ReplayGap_RefreshesFromAuthoritativeSnapshot()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("queue-gap", background: true));
        var token = Guid.NewGuid().ToString("N");
        await using var stale = await fixture.OpenProxyAsync(
            "queue-gap", AgentSessionOpenIntent.Start, attachmentToken: token);
        var disconnected = WaitForDisconnectAsync(stale.Client);
        await stale.Transport.DropConnectionsAsync();
        await disconnected;
        await stale.Chat.DisposeAsync();
        var owner = await fixture.OwnerChatAsync("queue-gap", 0);
        var ownerQueues = ((IAgentChat)owner).InputQueues;
        ownerQueues.CreateQueue(CreateQueue("authoritative", 0));

        await using var refreshed = await fixture.OpenProxyAsync(
            "queue-gap",
            AgentSessionOpenIntent.Attach,
            attachmentToken: token,
            cursor: new ReplayCursor
            {
                Epoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                Sequence = long.MaxValue,
            });

        AssertQueueStateEqual(ownerQueues.Snapshot, refreshed.Chat.InputQueues.Snapshot);
        Assert.Contains(refreshed.Chat.InputQueues.Snapshot.Queues, queue => queue.Name == "authoritative");
    }

    [Fact]
    public async Task Queues_ActiveRunEnqueue_UsesSameCommandAsFutureTurn()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("active", background: true));
        await using var proxy = await fixture.OpenProxyAsync("active", AgentSessionOpenIntent.Start);
        await proxy.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("held", 0, AgentInputQueueImmediacy.Held),
            TestContext.Current.CancellationToken);
        var heldQueue = proxy.Chat.InputQueues.Snapshot.Queues.Single(queue => queue.Name == "held");
        var stream = fixture.Model.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "working"));
        var terminal = stream.Complete(isReady: false);
        var owner = await fixture.OwnerChatAsync("active", 0);
        var ownerRunning = WaitForRunningItemsAsync(owner, 1);
        owner.EnqueueUserMessage("begin active turn");
        await stream.WaitForClaimedAsync(TestContext.Current.CancellationToken);
        await ownerRunning;
        await WaitForBusyStateAsync(proxy.Chat, isBusy: true);
        var ownerBecameIdle = WaitForRunningItemsAsync(owner, 0);

        await proxy.Chat.InputQueues.EnqueueAsync(Enqueue(
            heldQueue.QueueId, "during", proxy.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken);
        terminal.MarkReady();
        await terminal.WaitForClaimedAsync(TestContext.Current.CancellationToken);
        await ownerBecameIdle;
        await WaitForQueueRevisionAsync(
            proxy.Chat,
            ((IAgentChat)owner).InputQueues.Snapshot.Revision);
        await proxy.Chat.InputQueues.EnqueueAsync(Enqueue(
            heldQueue.QueueId, "future", proxy.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken);

        Assert.False(proxy.Chat.IsBusy);
        Assert.Empty(proxy.Chat.RunningItems);
        var enqueueFrames = fixture.Listener.LastChannel.ReceivedMessages
            .Select(AgentSessionProtocolCodec.DeserializeCommand)
            .OfType<EnqueueInputCommand>()
            .ToArray();
        Assert.Equal(2, enqueueFrames.Length);
        Assert.DoesNotContain(
            fixture.Listener.LastChannel.ReceivedMessages,
            value => value.GetRawText().Contains("steer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Disposal_ClientChannelLost_ReconnectWithinGrace_RuntimeAndChildrenSurvive()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("grace"));
        await using var proxy = await fixture.OpenProxyAsync("grace", AgentSessionOpenIntent.Start);
        var runtime = await fixture.RuntimeAsync("grace", 0);
        var resources = await AddOwnedRuntimeTreeAsync(runtime);
        var epoch = runtime.Epoch;
        var disconnected = WaitForDisconnectAsync(proxy.Client);

        await proxy.Transport.DropConnectionsAsync();
        await disconnected;
        Assert.False(resources.RootProcess.IsDisposed);
        Assert.False(resources.ChildProcess.IsDisposed);
        Assert.False(runtime.IsFenced);

        await proxy.Chat.ReconnectNowAsync(TestContext.Current.CancellationToken);

        Assert.True(proxy.Chat.IsConnected);
        Assert.Equal(epoch, proxy.Client.LastAppliedCursor!.Value.Epoch);
        Assert.Same(runtime, await fixture.RuntimeAsync("grace", 0));
        Assert.False(resources.RootProcess.IsDisposed);
        Assert.False(resources.ChildProcess.IsDisposed);
    }

    [Fact]
    public async Task Disposal_ClientChannelLost_GraceExpiresDefaultPolicy_DisposesRuntimeAndChildren()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("expiry"));
        await using var proxy = await fixture.OpenProxyAsync("expiry", AgentSessionOpenIntent.Start);
        var runtime = await fixture.RuntimeAsync("expiry", 0);
        var resources = await AddOwnedRuntimeTreeAsync(runtime);
        var oldEpoch = runtime.Epoch;
        var released = runtime.GetReleaseTask(proxy.OpenRequest.AttachmentToken, 1);
        var terminated = WaitForRuntimeTerminationAsync(runtime);
        var disconnected = WaitForDisconnectAsync(proxy.Client);
        await proxy.Transport.DropConnectionsAsync();
        await disconnected;
        await proxy.Chat.DisposeAsync();

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await released;
        await Task.WhenAll(resources.RootProcess.Disposed, resources.ChildProcess.Disposed);
        await terminated;

        Assert.True(runtime.IsFenced);
        Assert.Null(await fixture.TryRuntimeAsync("expiry", 0));
        var stopped = await fixture.ReadSessionAsync("expiry");
        Assert.Equal("stopped", stopped.GetProperty("runtime-state").GetString());
        Assert.Equal(oldEpoch.Value.ToString("D"), stopped.GetProperty("last-stopped-runtime-epoch").GetString());

        await using var restarted = await fixture.OpenProxyAsync("expiry", AgentSessionOpenIntent.Start);
        Assert.NotEqual(oldEpoch, restarted.Client.LastAppliedCursor!.Value.Epoch);
    }

    [Fact]
    public async Task Disposal_ClientChannelLost_GraceExpiresBackgroundEnabled_PreservesRuntimeAndChildren()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("background-expiry", background: true));
        var token = Guid.NewGuid().ToString("N");
        await using var proxy = await fixture.OpenProxyAsync(
            "background-expiry", AgentSessionOpenIntent.Start, attachmentToken: token);
        var runtime = await fixture.RuntimeAsync("background-expiry", 0);
        var resources = await AddOwnedRuntimeTreeAsync(runtime);
        var released = runtime.GetReleaseTask(token, 1);
        var disconnected = WaitForDisconnectAsync(proxy.Client);
        await proxy.Transport.DropConnectionsAsync();
        await disconnected;
        await proxy.Chat.DisposeAsync();

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await released;
        await using var reattached = await fixture.OpenProxyAsync(
            "background-expiry", AgentSessionOpenIntent.Attach);

        Assert.Same(runtime, await fixture.RuntimeAsync("background-expiry", 0));
        Assert.Equal(runtime.Epoch, reattached.Client.LastAppliedCursor!.Value.Epoch);
        Assert.False(resources.RootProcess.IsDisposed);
        Assert.False(resources.ChildProcess.IsDisposed);
    }

    [Fact]
    public async Task OpenSubagent_Authorized_ReturnsIndependentChildProxy()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("parent", background: true));
        await using var parent = await fixture.OpenProxyAsync("parent", AgentSessionOpenIntent.Start);
        var parentRuntime = await fixture.RuntimeAsync("parent", 0);
        var parentOwner = Assert.IsType<AgentChat>(parentRuntime.Chat);
        await parentOwner.GetOrCreateAsync(
            "child-agent",
            Definition("child"),
            "tool-call",
            TestContext.Current.CancellationToken);
        await fixture.SeedAsync(Session("child-agent"));

        var descriptor = await parent.Client.OpenSubagentAsync(new OpenAgentSubagentRequest
        {
            AgentId = "child-agent",
            CommandId = Guid.NewGuid(),
        }, TestContext.Current.CancellationToken);
        await using var child = await fixture.OpenProxyAsync(
            descriptor.AgentSessionId,
            AgentSessionOpenIntent.Attach,
            descriptor.OwningProfileEntityId,
            descriptor.OwnershipGeneration);
        var childRuntime = await fixture.RuntimeAsync(
            descriptor.AgentSessionId, descriptor.OwnershipGeneration);
        var childOwner = Assert.IsType<AgentChat>(childRuntime.Chat);
        var childResource = new TrackingOwnedProcess();
        childOwner.RegisterOwnedResource(childResource);
        await child.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("child-only", child.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken);

        Assert.Equal("child-agent", descriptor.AgentId);
        Assert.NotSame(parentRuntime, childRuntime);
        Assert.NotSame(parentOwner, childOwner);
        Assert.Equal("parent", parentRuntime.SessionId);
        Assert.Equal("child-agent", childRuntime.SessionId);
        Assert.NotEqual(parentRuntime.SessionId, childRuntime.SessionId);
        Assert.DoesNotContain(parent.Chat.InputQueues.Snapshot.Queues, queue => queue.Name == "child-only");

        await child.Chat.DetachAsync(TestContext.Current.CancellationToken);
        await childResource.Disposed;
        Assert.Null(await fixture.TryRuntimeAsync("child-agent", 0));
    }

    [Fact]
    public async Task ModalResponse_ConcurrentViewers_AcceptsFirstAndRejectsStaleSecond()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(Session("modal", background: true));
        await using var first = await fixture.OpenProxyAsync("modal", AgentSessionOpenIntent.Start);
        await using var second = await fixture.OpenProxyAsync("modal", AgentSessionOpenIntent.Attach);
        var firstVisible = WaitForModalCountAsync(first.Chat, 1);
        var secondVisible = WaitForModalCountAsync(second.Chat, 1);
        var owner = await fixture.OwnerChatAsync("modal", 0);
        owner.PublishModal(new AgentChatModal
        {
            Id = "approval",
            OwnerAgentId = "owner",
            Title = "Approve",
            Body = "Continue?",
            Content = new ApprovalModalContent
            {
                ApproveLabel = "Yes",
                RejectLabel = "No",
            },
        });
        await Task.WhenAll(firstVisible, secondVisible);
        var response = JsonSerializer.SerializeToElement(new { approved = true });
        var responseAttempts = WaitForModalResponseAttemptsAsync(owner, 2);
        var firstResponse = first.Chat.RespondToModalAsync(
            "approval", response, TestContext.Current.CancellationToken);
        var secondResponse = second.Chat.RespondToModalAsync(
            "approval", response, TestContext.Current.CancellationToken);
        await responseAttempts;
        var firstDismissed = WaitForModalCountAsync(first.Chat, 0);
        var secondDismissed = WaitForModalCountAsync(second.Chat, 0);
        owner.PublishModalDismiss("approval");
        var outcomes = await Task.WhenAll(
            ObserveFailureAsync(firstResponse),
            ObserveFailureAsync(secondResponse));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is RemoteAgentSessionException);
        await Task.WhenAll(firstDismissed, secondDismissed);
    }

    [Fact]
    public async Task Takeover_ActiveOldRuntime_FencesOldProxyBeforeNewRuntimeMutates()
    {
        var newOwner = "22222222-2222-2222-2222-222222222222";
        await using var fixture = await ScenarioFixture.CreateAsync(Session("takeover", background: true));
        await using var oldProxy = await fixture.OpenProxyAsync("takeover", AgentSessionOpenIntent.Start);
        var oldRuntime = await fixture.RuntimeAsync("takeover", 0);
        var oldTerminal = WaitForTerminalAsync(oldProxy.Chat);
        var oldChannelEof = oldProxy.Transport.LastChannelCompletion;
        await using var takeoverTransport = fixture.CreateTransport();
        var statusRequest = fixture.OpenRequest(
            "takeover",
            AgentSessionOpenIntent.Status,
            ScenarioFixture.DefaultOwner,
            0,
            Guid.NewGuid().ToString("N"));

        await RemoteAgentSessionClient.TakeOverAsync(
            takeoverTransport,
            statusRequest,
            newOwner,
            TestContext.Current.CancellationToken);
        await oldTerminal;
        await oldChannelEof;

        Assert.True(oldRuntime.IsFenced);
        await Assert.ThrowsAnyAsync<Exception>(() => oldProxy.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("old-rejected", oldProxy.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken));
        await using var newProxy = await fixture.OpenProxyAsync(
            "takeover", AgentSessionOpenIntent.Attach, newOwner, 1);
        var result = await newProxy.Chat.InputQueues.CreateQueueAsync(
            CreateQueue("new-owner", newProxy.Chat.InputQueues.Snapshot.Revision),
            TestContext.Current.CancellationToken);
        var persisted = await fixture.ReadSessionAsync("takeover");

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Equal(1, persisted.GetProperty("ownership-generation").GetInt64());
        Assert.Equal(newOwner, persisted.GetProperty("owning-profile-entity-id").GetString());
        Assert.NotEqual(oldRuntime.Epoch, newProxy.Client.LastAppliedCursor!.Value.Epoch);
        Assert.Equal(1, (await fixture.RuntimeAsync("takeover", 1)).SessionContext!.OwnershipGeneration);
    }

    [Fact]
    public async Task CurrentSessionContext_TwoRemoteSessions_DoNotCrossContaminate()
    {
        await using var fixture = await ScenarioFixture.CreateAsync(
            Session("context-a", background: true),
            Session("context-b", background: true));
        await using var first = await fixture.OpenProxyAsync("context-a", AgentSessionOpenIntent.Start);
        await using var second = await fixture.OpenProxyAsync("context-b", AgentSessionOpenIntent.Start);
        var firstRuntime = await fixture.RuntimeAsync("context-a", 0);
        var secondRuntime = await fixture.RuntimeAsync("context-b", 0);
        var firstOwner = Assert.IsType<AgentChat>(firstRuntime.Chat);
        var secondOwner = Assert.IsType<AgentChat>(secondRuntime.Chat);
        var firstContext = Assert.IsType<CurrentSessionContext>(firstRuntime.SessionContext);
        var secondContext = Assert.IsType<CurrentSessionContext>(secondRuntime.SessionContext);

        Assert.Equal("context-a", first.Chat.Information.AgentSessionId);
        Assert.Equal("context-b", second.Chat.Information.AgentSessionId);
        Assert.Equal("context-a", firstContext.AgentSessionId);
        Assert.Equal("context-b", secondContext.AgentSessionId);
        Assert.NotEqual(firstContext.RuntimeEpoch, secondContext.RuntimeEpoch);
        Assert.NotSame(firstOwner, secondOwner);
    }

    private static ScenarioSession Session(string id, bool background = false) =>
        new(id, ScenarioFixture.DefaultOwner, 0, background, Definition(id));

    private static AgentDefinition Definition(string name) =>
        AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{name}}",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);

    private static CreateAgentInputQueueRequest CreateQueue(
        string name,
        long revision,
        AgentInputQueueImmediacy immediacy = AgentInputQueueImmediacy.Queue) => new()
    {
        CommandId = Guid.NewGuid(),
        ExpectedRevision = revision,
        Configuration = new AgentInputQueueConfiguration
        {
            Name = name,
            Immediacy = immediacy,
            Priority = 10,
        },
    };

    private static EnqueueAgentInputRequest Enqueue(
        string queueId, string text, long revision) => new()
    {
        TargetQueueId = queueId,
        Messages = [new ChatMessage(ChatRole.User, text)],
        CommandId = Guid.NewGuid(),
        ExpectedRevision = revision,
    };

    private static async Task<AgentInputQueueCommandResult?> ObserveAsync(
        Task<AgentInputQueueCommandResult> operation)
    {
        try
        {
            return await operation;
        }
        catch (RemoteAgentSessionException)
        {
            return null;
        }
    }

    private static async Task<Exception?> ObserveFailureAsync(Task operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static string[] TextHistory(RemoteAgentChat chat) =>
        chat.History
            .SelectMany(item => item.Contents.OfType<TextContent>())
            .Select(content => content.Text)
            .ToArray();

    private static void AssertQueueStateEqual(
        AgentInputQueuesSnapshot expected,
        AgentInputQueuesSnapshot actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(
            expected.Queues.OrderBy(queue => queue.QueueId).Select(queue => (
                queue.QueueId,
                queue.Name,
                queue.IsDefault,
                queue.IsImmediate,
                queue.Immediacy,
                queue.Priority,
                queue.Revision,
                Items: string.Join(",", queue.Items.Select(item => item.ItemId)))),
            actual.Queues.OrderBy(queue => queue.QueueId).Select(queue => (
                queue.QueueId,
                queue.Name,
                queue.IsDefault,
                queue.IsImmediate,
                queue.Immediacy,
                queue.Priority,
                queue.Revision,
                Items: string.Join(",", queue.Items.Select(item => item.ItemId)))));
    }

    private static bool IsStrictlyIncreasing(IEnumerable<long> values)
    {
        var sequence = values.ToArray();
        return sequence.Length >= 2
            && sequence.Zip(sequence.Skip(1)).All(pair => pair.First < pair.Second);
    }

    private static Task WaitForDisconnectAsync(RemoteAgentSessionClient client)
    {
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnexpectedlyDisconnected += OnDisconnected;
        return WaitAndUnsubscribeAsync(disconnected.Task, Remove);

        void OnDisconnected(object? sender, EventArgs args) => disconnected.TrySetResult();
        void Remove() => client.UnexpectedlyDisconnected -= OnDisconnected;
    }

    private static Task WaitForQueueRevisionAsync(RemoteAgentChat chat, long revision)
    {
        if (chat.InputQueues.Snapshot.Revision >= revision)
            return Task.CompletedTask;
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.InputQueues.Changed += OnChanged;
        OnChanged(null, EventArgs.Empty);
        return WaitAndUnsubscribeAsync(changed.Task, Remove);

        void OnChanged(object? sender, EventArgs args)
        {
            if (chat.InputQueues.Snapshot.Revision >= revision)
                changed.TrySetResult();
        }
        void Remove() => chat.InputQueues.Changed -= OnChanged;
    }

    private static Task WaitForHistoryCountAsync(RemoteAgentChat chat, int count)
    {
        if (chat.History.Count >= count)
            return Task.CompletedTask;
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.History).CollectionChanged += OnChanged;
        OnChanged(null, default!);
        return WaitAndUnsubscribeAsync(changed.Task, Remove);

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.History.Count >= count)
                changed.TrySetResult();
        }
        void Remove() => ((INotifyCollectionChanged)chat.History).CollectionChanged -= OnChanged;
    }

    private static Task WaitForModalCountAsync(RemoteAgentChat chat, int count)
    {
        if (chat.Modals.Count == count)
            return Task.CompletedTask;
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.Modals).CollectionChanged += OnChanged;
        OnChanged(null, default!);
        return WaitAndUnsubscribeAsync(changed.Task, Remove);

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.Modals.Count == count)
                changed.TrySetResult();
        }
        void Remove() => ((INotifyCollectionChanged)chat.Modals).CollectionChanged -= OnChanged;
    }

    private static Task WaitForModalResponseAttemptsAsync(AgentChat chat, int count)
    {
        var attempts = 0;
        var reached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.ModalResponseAttempted += OnAttempted;
        return WaitAndUnsubscribeAsync(reached.Task, Remove);

        void OnAttempted(object? sender, EventArgs args)
        {
            if (Interlocked.Increment(ref attempts) == count)
                reached.TrySetResult();
        }
        void Remove() => chat.ModalResponseAttempted -= OnAttempted;
    }

    private static Task WaitForBusyStateAsync(RemoteAgentChat chat, bool isBusy)
    {
        if (chat.IsBusy == isBusy)
            return Task.CompletedTask;
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.RuntimeStateChanged += OnChanged;
        OnChanged(null, EventArgs.Empty);
        return WaitAndUnsubscribeAsync(changed.Task, Remove);

        void OnChanged(object? sender, EventArgs args)
        {
            if (chat.IsBusy == isBusy)
                changed.TrySetResult();
        }
        void Remove() => chat.RuntimeStateChanged -= OnChanged;
    }

    private static Task WaitForRunningItemsAsync(IAgentChat chat, int count)
    {
        if (chat.RunningItems.Count == count)
            return Task.CompletedTask;
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = (INotifyCollectionChanged)chat.RunningItems;
        collection.CollectionChanged += OnChanged;
        OnChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return WaitAndUnsubscribeAsync(changed.Task, Remove);

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.RunningItems.Count == count)
                changed.TrySetResult();
        }
        void Remove() => collection.CollectionChanged -= OnChanged;
    }

    private static Task WaitForRuntimeTerminationAsync(RemoteAgentSessionLease runtime)
    {
        if (runtime.HasTerminated)
            return Task.CompletedTask;
        var terminated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Terminated += OnTerminated;
        if (runtime.HasTerminated)
            terminated.TrySetResult();
        return WaitAndUnsubscribeAsync(terminated.Task, Remove);

        void OnTerminated(object? sender, EventArgs args) => terminated.TrySetResult();
        void Remove() => runtime.Terminated -= OnTerminated;
    }

    private static Task WaitForTerminalAsync(RemoteAgentChat chat)
    {
        if (chat.IsTerminal)
            return Task.CompletedTask;
        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        chat.RuntimeStateChanged += OnChanged;
        OnChanged(null, EventArgs.Empty);
        return WaitAndUnsubscribeAsync(changed.Task, Remove);

        void OnChanged(object? sender, EventArgs args)
        {
            if (chat.IsTerminal)
                changed.TrySetResult();
        }
        void Remove() => chat.RuntimeStateChanged -= OnChanged;
    }

    private static async Task WaitAndUnsubscribeAsync(Task task, Action unsubscribe)
    {
        try
        {
            await task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            unsubscribe();
        }
    }

    private static async Task<RuntimeResources> AddOwnedRuntimeTreeAsync(
        RemoteAgentSessionLease runtime)
    {
        var owner = Assert.IsType<AgentChat>(runtime.Chat);
        var rootProcess = new TrackingOwnedProcess();
        owner.RegisterOwnedResource(rootProcess);
        await owner.GetOrCreateAsync(
            "owned-child",
            Definition("owned-child"),
            "owned-tool-call",
            TestContext.Current.CancellationToken);
        var child = Assert.IsType<AgentChat>(
            Assert.Single(owner.SubAgents, subagent => subagent.AgentId == "owned-child"));
        var childProcess = new TrackingOwnedProcess();
        child.RegisterOwnedResource(childProcess);
        return new RuntimeResources(rootProcess, childProcess);
    }

    private sealed record ScenarioSession(
        string SessionId,
        string Owner,
        long Generation,
        bool ContinueInBackground,
        AgentDefinition Definition);

    private sealed record RuntimeResources(
        TrackingOwnedProcess RootProcess,
        TrackingOwnedProcess ChildProcess);

    private sealed class TrackingOwnedProcess : IAsyncDisposable
    {
        private readonly TaskCompletionSource disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposeCount;

        internal bool IsDisposed => Volatile.Read(ref this.disposeCount) != 0;
        internal Task Disposed => this.disposed.Task;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref this.disposeCount) == 1)
                this.disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScenarioFixture : IAsyncDisposable
    {
        internal const string DefaultOwner = "11111111-1111-1111-1111-111111111111";

        private readonly InMemoryDataAccessLayer data;
        private readonly AgentChatFactory chatFactory;
        private readonly RunningAgentChatTable runningChats;
        private readonly RemoteAgentSessionRuntimeRegistry runtimes;
        private readonly AgentSessionTransportListener productionListener;
        private readonly TransportRegistry transportRegistry = new();
        private readonly Dictionary<string, EntityId> sessionEntities = new(StringComparer.Ordinal);
        private readonly List<ReconnectableLocalTransport> transports = [];

        private ScenarioFixture()
        {
            this.data = new InMemoryDataAccessLayer(timeProvider: this.Time);
            this.chatFactory = new AgentChatFactory(
                new InMemoryAgentPersistenceStore(Time),
                new AgentServices { ChatClientOverride = this.Model },
                TaskScheduler.Default);
            this.runningChats = new RunningAgentChatTable(this.chatFactory);
            this.runtimes = new RemoteAgentSessionRuntimeRegistry(Time);
            var runtimeFactory = new DataAccessAgentSessionRuntimeHostFactory(
                this.data,
                this.runningChats,
                new AgentSessionRuntimeContextFactory(null),
                Time,
                new AgentServices { ChatClientOverride = this.Model });
            var host = new RemoteAgentSessionHost(
                new AllowAllAuthorizer(),
                this.runtimes,
                runtimeFactory);
            this.productionListener = new AgentSessionTransportListener(
                host,
                new FixedPeerIdentityProvider());
            this.Listener = new ObservableTransportListener(this.productionListener);
            this.transportRegistry.Register(this.Listener);
        }

        internal FakeTimeProvider Time { get; } =
            new(DateTimeOffset.Parse("2026-09-14T00:00:00Z"));
        internal DeterministicTestChatClient Model { get; } = new();
        internal ObservableTransportListener Listener { get; }

        internal static async Task<ScenarioFixture> CreateAsync(params ScenarioSession[] sessions)
        {
            var fixture = new ScenarioFixture();
            foreach (var session in sessions)
                await fixture.SeedAsync(session);
            return fixture;
        }

        internal async Task SeedAsync(ScenarioSession session)
        {
            var entityData = AgentSessionEntityFactory.CreateEntityData(
                new CreateAgentSessionEntityDataRequest
                {
                    AgentDefinitionEntityId = new EntityId(),
                    AgentDisplayName = session.Definition.DisplayName ?? session.SessionId,
                    AgentSessionId = session.SessionId,
                    AgentSessionNames = [new EntityName("tests", session.SessionId)],
                    CurrentTime = Time.GetUtcNow(),
                    ComputerName = "scenario-host",
                    HostProfileEntityId = new EntityId(session.Owner),
                    OwnershipGeneration = session.Generation,
                    ContinueInBackground = session.ContinueInBackground,
                });
            var values = entityData.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
            values["definition"] = JsonDocument.Parse(session.Definition.ToJson()).RootElement.Clone();
            var data = JsonSerializer.SerializeToElement(values);
            var entityId = new EntityId(data.GetProperty("entity-id").GetString()!);
            var result = await this.data.UpdateAsync(new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = $"Seed {session.SessionId}" },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = data,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            }, TestContext.Current.CancellationToken);
            Assert.Empty(Assert.Single(result.EntityResults).Errors);
            this.sessionEntities.Add(session.SessionId, entityId);
        }

        internal ReconnectableLocalTransport CreateTransport()
        {
            var transport = new ReconnectableLocalTransport(this.transportRegistry);
            this.transports.Add(transport);
            return transport;
        }

        internal async Task<ProxyHandle> OpenProxyAsync(
            string sessionId,
            AgentSessionOpenIntent intent,
            string owner = DefaultOwner,
            long generation = 0,
            string? attachmentToken = null,
            ReplayCursor? cursor = null)
        {
            var transport = this.CreateTransport();
            var client = new RemoteAgentSessionClient(transport, Time);
            var request = this.OpenRequest(
                sessionId,
                intent,
                owner,
                generation,
                attachmentToken ?? Guid.NewGuid().ToString("N"),
                cursor);
            var chat = await RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
            {
                Client = client,
                OpenRequest = request,
                ForegroundScheduler = TaskScheduler.Default,
            }, TestContext.Current.CancellationToken);
            return new ProxyHandle(transport, client, chat, request);
        }

        internal AgentSessionOpenRequest OpenRequest(
            string sessionId,
            AgentSessionOpenIntent intent,
            string owner,
            long generation,
            string attachmentToken,
            ReplayCursor? cursor = null) => new()
        {
            ProtocolVersion = 1,
            AgentSessionId = sessionId,
            ExpectedOwningProfileEntityId = owner,
            ExpectedOwnershipGeneration = generation,
            OpenIntent = intent,
            AttachmentToken = attachmentToken,
            Capabilities = [],
            ReplayCursor = cursor,
        };

        internal async Task<RemoteAgentSessionLease> RuntimeAsync(
            string sessionId, long generation) =>
            await this.TryRuntimeAsync(sessionId, generation)
            ?? throw new Xunit.Sdk.XunitException("Expected a running remote session.");

        internal ValueTask<RemoteAgentSessionLease?> TryRuntimeAsync(
            string sessionId, long generation) =>
            this.runtimes.TryGetAsync(
                sessionId, generation, TestContext.Current.CancellationToken);

        internal async Task<AgentChat> OwnerChatAsync(string sessionId, long generation) =>
            Assert.IsType<AgentChat>((await this.RuntimeAsync(sessionId, generation)).Chat);

        internal async Task<JsonElement> ReadSessionAsync(string sessionId)
        {
            var result = await this.data.GetAsync(new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest { EntityId = this.sessionEntities[sessionId] },
                ],
            }, TestContext.Current.CancellationToken);
            return Assert.Single(result.Batches.SelectMany(batch => batch.Entities)).Data!.Value;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var transport in this.transports)
                await transport.DisposeAsync();
            await this.productionListener.DisposeAsync();
            await this.chatFactory.DisposeAsync();
        }
    }

    private sealed class ProxyHandle(
        ReconnectableLocalTransport transport,
        RemoteAgentSessionClient client,
        RemoteAgentChat chat,
        AgentSessionOpenRequest openRequest) : IAsyncDisposable
    {
        internal ReconnectableLocalTransport Transport { get; } = transport;
        internal RemoteAgentSessionClient Client { get; } = client;
        internal RemoteAgentChat Chat { get; } = chat;
        internal AgentSessionOpenRequest OpenRequest { get; } = openRequest;

        public async ValueTask DisposeAsync()
        {
            await this.Chat.DisposeAsync();
            await this.Transport.DisposeAsync();
        }
    }

    private sealed class ReconnectableLocalTransport(TransportRegistry registry) : ITransport
    {
        private readonly object gate = new();
        private readonly List<LocalTransport> connections = [];
        private bool disposed;

        internal Task LastChannelCompletion { get; private set; } = Task.CompletedTask;

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request, CancellationToken ct = default)
        {
            LocalTransport connection;
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                connection = new LocalTransport(registry);
                this.connections.Add(connection);
            }
            var channel = await connection.ConnectToMessageChannelAsync(request, ct);
            this.LastChannelCompletion = channel.Reader.Completion;
            return channel;
        }

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        internal async Task DropConnectionsAsync()
        {
            LocalTransport[] current;
            lock (this.gate)
            {
                current = this.connections.ToArray();
                this.connections.Clear();
            }
            foreach (var connection in current)
                await connection.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            lock (this.gate)
            {
                if (this.disposed)
                    return;
                this.disposed = true;
            }
            await this.DropConnectionsAsync();
        }
    }

    private sealed class ObservableTransportListener(AgentSessionTransportListener inner)
        : ITransportListener
    {
        internal ObservableChannel LastChannel { get; private set; } = null!;

        public Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            this.LastChannel = new ObservableChannel(channel);
            return inner.OnChannelOpenAsync(request, this.LastChannel, ct);
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(
            JsonElement request, Stream stream, CancellationToken ct = default) =>
            inner.OnStreamOpenAsync(request, stream, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservableChannel : IMessageChannel
    {
        private readonly IMessageChannel inner;
        private readonly ObservableWriter writer;
        private readonly RecordingReader reader;

        internal ObservableChannel(IMessageChannel inner)
        {
            this.inner = inner;
            this.writer = new ObservableWriter(inner.Writer);
            this.reader = new RecordingReader(inner.Reader);
        }

        public ChannelWriter<JsonElement> Writer => this.writer;
        public ChannelReader<JsonElement> Reader => this.reader;
        internal IReadOnlyList<JsonElement> ReceivedMessages => this.reader.Messages;
        internal AsyncWriteGate ArmNextAsyncWrite() => this.writer.ArmNext();
        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }

    private sealed class RecordingReader(ChannelReader<JsonElement> inner)
        : ChannelReader<JsonElement>
    {
        private readonly ConcurrentQueue<JsonElement> messages = [];

        internal IReadOnlyList<JsonElement> Messages => this.messages.ToArray();
        public override Task Completion => inner.Completion;
        public override bool TryRead(out JsonElement item)
        {
            if (!inner.TryRead(out item))
                return false;
            this.messages.Enqueue(item.Clone());
            return true;
        }
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToReadAsync(cancellationToken);
    }

    private sealed class ObservableWriter(ChannelWriter<JsonElement> inner)
        : ChannelWriter<JsonElement>
    {
        private readonly object gate = new();
        private AsyncWriteGate? next;

        internal AsyncWriteGate ArmNext()
        {
            lock (this.gate)
            {
                Assert.Null(this.next);
                return this.next = new AsyncWriteGate();
            }
        }

        public override bool TryComplete(Exception? error = null) =>
            inner.TryComplete(error);

        public override bool TryWrite(JsonElement item) =>
            inner.TryWrite(item);

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);

        public override async ValueTask WriteAsync(
            JsonElement item, CancellationToken cancellationToken = default)
        {
            AsyncWriteGate? armed;
            lock (this.gate)
            {
                armed = this.next;
                this.next = null;
            }
            if (armed is not null)
            {
                armed.MarkStarted();
                await armed.WaitForReleaseAsync(cancellationToken);
            }
            await inner.WriteAsync(item, cancellationToken);
            armed?.MarkCompleted();
        }
    }

    private sealed class AsyncWriteGate
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => this.started.Task.WaitAsync(TestContext.Current.CancellationToken);
        internal Task Completed => this.completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        internal void MarkStarted() => this.started.TrySetResult();
        internal void Release() => this.release.TrySetResult();
        internal Task WaitForReleaseAsync(CancellationToken ct) => this.release.Task.WaitAsync(ct);
        internal void MarkCompleted() => this.completed.TrySetResult();
    }

    private sealed class AllowAllAuthorizer : IAgentSessionAttachAuthorizer
    {
        public ValueTask<AgentSessionAuthorizationDecision> AuthorizeAsync(
            TransportPeerIdentity peer,
            AgentSessionAuthorizationRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentSessionAuthorizationDecision { IsAllowed = true });
        }
    }

    private sealed class FixedPeerIdentityProvider : ITransportPeerIdentityProvider
    {
        public TransportPeerIdentity GetRequiredIdentity(IMessageChannel channel) => new()
        {
            AuthenticationScheme = "scenario",
            StablePeerId = "gui-client",
            UserEntityId = ScenarioFixture.DefaultOwner,
        };
    }
}
