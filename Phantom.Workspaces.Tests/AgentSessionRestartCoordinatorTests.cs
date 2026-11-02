using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Local;
using System.Threading.Channels;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Fact]
    public async Task RestartSession_RealOwnerFactory_PreservesPendingQueueAndReattachesEveryLocalViewer()
    {
        await using var environment = new RestartEnvironment();
        await using var first = await environment.AcquireAsync();
        await using var second = await environment.AcquireAsync();
        var original = Assert.IsType<AgentChat>(first.AgentChat);
        var originalRow = Assert.Single(environment.Table.RunningSessions);
        var originalInformation = JsonSerializer.Serialize(original.Information, AIJsonUtilities.DefaultOptions);
        var queue = original.QueueManager.CreateInputQueue("Preserved", AgentInputQueueImmediacy.Held);
        original.EnqueueUserMessage("pending input", queue);
        var reattached = new List<RunningAgentChatLease>();
        first.ReattachAsync = replacement => { reattached.Add(replacement); return Task.CompletedTask; };
        second.ReattachAsync = replacement => { reattached.Add(replacement); return Task.CompletedTask; };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = first.RestartIdentity!,
        };

        var result = await first.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.NotEqual(request.ExpectedRuntime.RuntimeEpoch, result.ReplacementRuntime!.RuntimeEpoch);
        Assert.Equal(request.ExpectedRuntime.OwnershipGeneration, result.ReplacementRuntime.OwnershipGeneration);
        Assert.Equal(2, reattached.Count);
        Assert.Same(reattached[0].AgentChat, reattached[1].AgentChat);
        Assert.NotSame(original, reattached[0].AgentChat);
        Assert.Equal(originalInformation, JsonSerializer.Serialize(
            reattached[0].AgentChat.Information, AIJsonUtilities.DefaultOptions));
        var replacementRow = Assert.Single(environment.Table.RunningSessions);
        Assert.Equal(originalRow.EntityName, replacementRow.EntityName);
        Assert.Equal(originalRow.EntityId, replacementRow.EntityId);
        Assert.Equal(originalRow.WorkspaceId, replacementRow.WorkspaceId);
        Assert.True(replacementRow.ContinueInBackground);
        var preserved = Assert.Single(reattached[0].AgentChat.InputQueues.Snapshot.Queues,
            item => item.Name == "Preserved");
        Assert.Equal(queue.Queue.QueueId, preserved.QueueId);
        Assert.Single(preserved.Items);
        Assert.True(original.IsTerminal);
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Single(environment.Table.RunningSessions);
        foreach (var lease in reattached)
            await lease.DisposeAsync();
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_ConcurrentCommandsCoalesceAndLostReplyReconcilesFromPersistence()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        var results = await Task.WhenAll(
            viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken),
            viewer.RestartRuntimeAsync!(request with { CommandId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        Assert.All(results, result => Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State));
        Assert.Equal(results[0].ReplacementRuntime, results[1].ReplacementRuntime);
        var recoveredFactory = new DataAccessAgentSessionRuntimeHostFactory(
            environment.Layer.Object,
            environment.Table,
            new AgentSessionRuntimeContextFactory(null),
            environment.Time);

        var recovered = await recoveredFactory.RestartAsync(
            request, environment.Registry, TestContext.Current.CancellationToken);

        Assert.Equal(results[0], recovered);
        Assert.Single(environment.Table.RunningSessions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recoveredFactory.RestartAsync(
            request with
            {
                ExpectedRuntime = request.ExpectedRuntime with
                {
                    RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
                },
            }, environment.Registry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_CancellationAfterDurableCommitDoesNotStrandReplacement()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        environment.OnUpdate = request =>
        {
            if (request.UpdateMetadata.Comment.Text == "Record agent session restart operation")
                cancellation.Cancel();
        };

        var result = await viewer.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        }, cancellation.Token);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.Single(environment.Table.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_CheckpointPersistenceFailureLeavesOriginalUsable()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var original = Assert.IsType<AgentChat>(viewer.AgentChat);
        environment.OnUpdate = request =>
        {
            if (request.UpdateMetadata.Comment.Text == "Record agent session restart operation")
                throw new IOException("Journal unavailable.");
        };

        await Assert.ThrowsAsync<IOException>(() => viewer.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        }, TestContext.Current.CancellationToken));

        Assert.False(original.IsTerminal);
        original.EnqueueUserMessage("input",
            original.QueueManager.CreateInputQueue("Still usable", AgentInputQueueImmediacy.Held));
        Assert.Single(environment.Table.RunningSessions);
        environment.OnUpdate = null;
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_FailedStartupIsStoppedAndRetryPreservesCheckpoint()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var original = Assert.IsType<AgentChat>(viewer.AgentChat);
        var queue = original.QueueManager.CreateInputQueue("Pending retry", AgentInputQueueImmediacy.Held);
        original.EnqueueUserMessage("preserved retry input", queue);
        environment.FailStartup = true;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };

        var stopped = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Stopped, stopped.State);
        Assert.Empty(environment.Table.RunningSessions);
        environment.FailStartup = false;
        var retried = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, retried.State);
        Assert.Equal(stopped.ReplacementRuntime, retried.ReplacementRuntime);
        var runtime = await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken);
        Assert.NotNull(runtime);
        Assert.Single(runtime.Chat.InputQueues.Snapshot.Queues.Single(item => item.QueueId == queue.Queue.QueueId).Items);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_StaleEpochDoesNotTerminateCurrentRuntime()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();

        var result = await viewer.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity! with
            {
                RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.TargetChanged, result.State);
        Assert.False(Assert.IsType<AgentChat>(viewer.AgentChat).IsTerminal);
        Assert.Single(environment.Table.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_ChildCapabilityFollowsIndependentAuthorityRatherThanTabType()
    {
        await using var childHost = new RestartEnvironment();
        await using var parentHost = new RestartEnvironment();
        await using var childViewer = await childHost.AcquireAsync();
        await using var parentViewer = await parentHost.AcquireAsync();
        var child = (await childHost.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        var parent = (await parentHost.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = childViewer.RestartIdentity!,
        };
        Assert.True(child.CanRestartIndependently);
        using (await child.EnterRestartTransitionAsync(TestContext.Current.CancellationToken))
            Assert.False(parent.TryKeepChildAlive(child));
        Assert.True(parent.TryKeepChildAlive(child));
        Assert.False(child.CanRestartIndependently);
        Assert.Null(childViewer.RestartIdentityProvider!());
        await using var remote = await childHost.AttachRemoteAsync();
        Assert.Null(remote.RestartClient.RestartIdentity);
        Assert.Contains("shares its parent's lifecycle", remote.RestartClient.RestartDisabledReason!);
        Assert.Equal(AgentSessionOwnerRestartState.TargetChanged,
            (await childViewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        Assert.Null(await childViewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken));

        await parent.DisposeAsync();

        Assert.True(child.CanRestartIndependently);
        Assert.NotNull(childViewer.RestartIdentityProvider!());
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await childViewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_FinalFrameworkPersistenceFailureBlocksReplacementAndNotifiesRemoteViewer()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        await using var remote = await environment.AttachRemoteAsync();
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RuntimeStateChanged += (_, _) =>
        {
            if (remote.IsTerminal)
                unavailable.TrySetResult();
        };
        environment.FailFinalPersistence = true;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };

        var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Blocked, result.State);
        Assert.Empty(environment.Table.RunningSessions);
        await unavailable.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains(remote.History, item => item.Contents.OfType<TextContent>().Any(content =>
            content.Text.Contains("Replacement is blocked", StringComparison.Ordinal)));
        environment.FailFinalPersistence = false;
        var retry = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Blocked, retry.State);
        Assert.Empty(environment.Table.RunningSessions);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_UnauthorizedPeerCannotRestartOrReadJournal()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var before = environment.Persisted;
        var unauthorized = new TransportPeerIdentity
        {
            AuthenticationScheme = "local-workspace-session",
            StablePeerId = "other-user",
            UserEntityId = "66666666-6666-6666-6666-666666666666",
            UserComputerProfileEntityId = Owner,
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };

        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() => environment.Host.RestartAsync(
            unauthorized, request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() => environment.Host.GetRestartStatusAsync(
            unauthorized, request, TestContext.Current.CancellationToken));

        Assert.Equal(before, environment.Persisted);
        Assert.False(Assert.IsType<AgentChat>(viewer.AgentChat).IsTerminal);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_FreshHostDoesNotRecoverUnconfirmedShutdownAsStopped()
    {
        var oldHost = new RestartEnvironment();
        await using var oldViewer = await oldHost.AcquireAsync();
        JsonElement? committed = null;
        oldHost.OnUpdate = update =>
        {
            if (update.UpdateMetadata.Comment.Text == "Record agent session restart operation" && committed is null)
                committed = Assert.Single(update.Changes).Data!.Value.Clone();
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = oldViewer.RestartIdentity!,
        };
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await oldViewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        await oldHost.DisposeAsync();
        Assert.NotNull(committed);
        await using var recovered = new RestartEnvironment(oldHost.Store, oldHost.Persisted with { Data = committed });
        recovered.Time.Advance(TimeSpan.FromSeconds(31));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.AcquireAsync());

        Assert.Contains("shutdown was not durably confirmed", error.Message);
        Assert.Empty(recovered.Table.RunningSessions);
        var journal = recovered.Persisted.Data!.Value.GetProperty("restart-operations")
            .Deserialize<Dictionary<string, DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                AIJsonUtilities.DefaultOptions)!;
        Assert.Equal(AgentSessionOwnerRestartState.Blocked, Assert.Single(journal.Values).State);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_FreshHostRecoversPersistedStoppedCheckpoint()
    {
        var originalHost = new RestartEnvironment();
        await using var viewer = await originalHost.AcquireAsync();
        var chat = Assert.IsType<AgentChat>(viewer.AgentChat);
        var queue = chat.QueueManager.CreateInputQueue("Recovered", AgentInputQueueImmediacy.Held);
        chat.EnqueueUserMessage("durable pending input", queue);
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        originalHost.FailStartup = true;
        var stopped = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Stopped, stopped.State);
        await originalHost.DisposeAsync();
        await using var recoveredHost = new RestartEnvironment(originalHost.Store, originalHost.Persisted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recoveredHost.AcquireAsync());
        recoveredHost.Time.Advance(TimeSpan.FromSeconds(31));

        await using var recoveredViewer = await recoveredHost.AcquireAsync();

        var recoveredChat = Assert.IsType<AgentChat>(recoveredViewer.AgentChat);
        Assert.NotSame(chat, recoveredChat);
        Assert.Single(((IAgentChat)recoveredChat).InputQueues.Snapshot.Queues
            .Single(value => value.QueueId == queue.Queue.QueueId).Items);
        var status = await recoveredHost.Host.GetRestartStatusAsync(new TransportPeerIdentity
        {
            AuthenticationScheme = "local-workspace-session",
            StablePeerId = "local",
            UserEntityId = "55555555-5555-5555-5555-555555555555",
            UserComputerProfileEntityId = Owner,
        }, request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, status!.State);
        Assert.Equal(status.ReplacementRuntime, recoveredViewer.RestartIdentity);
        Assert.False(recoveredChat.InputQueueManager.IsRuntimeHandoff);
        var reconciliation = new DataAccessAgentSessionRuntimeHostFactory(
            recoveredHost.Layer.Object, recoveredHost.Table,
            new AgentSessionRuntimeContextFactory(null), recoveredHost.Time);
        Assert.True(await reconciliation.CanReconcileEpochAsync(
            "session", stopped.ReplacementRuntime!.RuntimeEpoch,
            status.ReplacementRuntime!.RuntimeEpoch, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_LostPersistedSuccessReplyReadsDurableOutcomeWithoutRepeating()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var lost = false;
        environment.OnUpdated = update =>
        {
            if (lost || update.UpdateMetadata.Comment.Text != "Record agent session restart operation")
                return;
            var operations = Assert.Single(update.Changes).Data!.Value.GetProperty("restart-operations")
                .Deserialize<Dictionary<string, DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(AIJsonUtilities.DefaultOptions)!;
            if (operations.Values.Any(value => value.State == AgentSessionOwnerRestartState.Succeeded))
            {
                lost = true;
                throw new IOException("Persisted result reply lost.");
            }
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };

        var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.True(lost);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!.Chat;
        Assert.Equal(result, await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken));
        Assert.Equal(result, await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
        Assert.Same(current, (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!.Chat);
        Assert.False(Assert.IsType<AgentChat>(current).InputQueueManager.IsRuntimeHandoff);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_LostFinalJournalAcknowledgementReconcilesWithoutAnotherReplacement()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        var failed = false;
        environment.OnUpdate = update =>
        {
            if (failed || update.UpdateMetadata.Comment.Text != "Record agent session restart operation")
                return;
            var operations = Assert.Single(update.Changes).Data!.Value.GetProperty("restart-operations")
                .Deserialize<Dictionary<string, DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                    AIJsonUtilities.DefaultOptions)!;
            if (operations.Values.Any(value => value.State == AgentSessionOwnerRestartState.Succeeded))
            {
                failed = true;
                throw new IOException("Final journal acknowledgement lost.");
            }
        };

        var stopped = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Stopped, stopped.State);
        var published = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!.Chat;
        var reconciled = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, reconciled.State);
        Assert.Same(published, (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!.Chat);
    }

    [Fact]
    public async Task RestartSession_RealOwnerFactory_PrecommitCancellationLeavesOriginalUsableAndHasNoJournal()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewer.RestartRuntimeAsync!(request, cancellation.Token));

        Assert.False(Assert.IsType<AgentChat>(viewer.AgentChat).IsTerminal);
        Assert.Null(await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_RealOwnerFactory_RemoteViewersReattachBeforePendingDispatchResumes(bool dropHandoff)
    {
        await using var environment = new RestartEnvironment(dropHandoff: dropHandoff);
        await using var local = await environment.AcquireAsync();
        var oldChat = Assert.IsType<AgentChat>(local.AgentChat);
        var preservedQueue = oldChat.QueueManager.CreateInputQueue("Remote pending", AgentInputQueueImmediacy.Held);
        oldChat.EnqueueUserMessage("preserved on every projection", preservedQueue);
        await using var first = await environment.AttachRemoteAsync();
        await using var second = await environment.AttachRemoteAsync();
        var expected = first.RestartClient.RestartIdentity!;
        var firstReattached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReattached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.RestartClient.UnexpectedlyDisconnected += (_, _) => firstDisconnected.TrySetResult();
        second.RestartClient.UnexpectedlyDisconnected += (_, _) => secondDisconnected.TrySetResult();
        first.InformationChanged += (_, _) =>
        {
            if (first.RestartClient.RestartIdentity?.RuntimeEpoch != expected.RuntimeEpoch)
                firstReattached.TrySetResult();
        };
        second.InformationChanged += (_, _) =>
        {
            if (second.RestartClient.RestartIdentity?.RuntimeEpoch != expected.RuntimeEpoch)
                secondReattached.TrySetResult();
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = expected,
        };

        var result = await first.RestartClient.RestartAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        await Task.WhenAll(firstDisconnected.Task, secondDisconnected.Task).WaitAsync(TestContext.Current.CancellationToken);
        var runtime = await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken);
        var chat = Assert.IsType<AgentChat>(runtime!.Chat);
        Assert.True(chat.InputQueueManager.IsRuntimeHandoff);
        await first.ReconnectNowAsync(TestContext.Current.CancellationToken);
        Assert.True(chat.InputQueueManager.IsRuntimeHandoff);
        await second.ReconnectNowAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(firstReattached.Task, secondReattached.Task).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(result.ReplacementRuntime, first.RestartClient.RestartIdentity);
        Assert.Equal(result.ReplacementRuntime, second.RestartClient.RestartIdentity);
        Assert.Single(first.InputQueues.Snapshot.Queues.Single(queue => queue.QueueId == preservedQueue.Queue.QueueId).Items);
        Assert.Single(second.InputQueues.Snapshot.Queues.Single(queue => queue.QueueId == preservedQueue.Queue.QueueId).Items);
        var dispatchResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.InputQueueManager.QueueStateChanged += (_, _) =>
        {
            if (!chat.InputQueueManager.IsRuntimeHandoff)
                dispatchResumed.TrySetResult();
        };
        if (!chat.InputQueueManager.IsRuntimeHandoff)
            dispatchResumed.TrySetResult();
        await dispatchResumed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(result, await second.RestartClient.GetRestartStatusAsync(
            request, TestContext.Current.CancellationToken));
        Assert.Single(environment.Table.RunningSessions);
    }

    internal sealed class RestartEnvironment : IAsyncDisposable
    {
        private readonly object gate = new();
        private QueryEntitySnapshot current = Entity();
        private int revision;
        internal FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        internal Mock<IDataAccessLayer> Layer { get; } = new();
        internal RunningAgentChatTable Table { get; }
        internal RemoteAgentSessionRuntimeRegistry Registry { get; }
        internal AgentChatFactory ChatFactory { get; }
        internal RemoteAgentSessionHost Host { get; }
        private readonly TransportPeerIdentityProvider peerIdentities = new();
        private readonly TransportRegistry listeners = new();
        private readonly LocalTransportFactory localTransportFactory;
        internal Action<UpdateRequest>? OnUpdate { get; set; }
        internal Action<UpdateRequest>? OnUpdated { get; set; }
        internal bool FailStartup { get; set; }
        internal bool FailFinalPersistence { get; set; }
        internal InMemoryAgentPersistenceStore Store { get; }
        internal QueryEntitySnapshot Persisted => this.current;

        internal RestartEnvironment(
            InMemoryAgentPersistenceStore? store = null, QueryEntitySnapshot? persisted = null, bool dropHandoff = false,
            TaskScheduler? foregroundScheduler = null)
        {
            this.current = persisted ?? Entity();
            this.Store = store ?? new InMemoryAgentPersistenceStore();
            this.Layer.Setup(layer => layer.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    lock (this.gate)
                        return new QueryResult
                        {
                            AuthoritativeTimestamp = new Timestamp(this.Time.GetUtcNow(), "query"),
                            Batches = [new TimestampedQueryBatch
                            {
                                Timestamp = null,
                                Entities =
                                [
                                    this.current,
                                    this.current with
                                    {
                                        EntityId = new EntityId(Owner),
                                        Data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                                        {
                                            ["entity-types"] = new[] { "user-computer-profile" },
                                            ["user-entity-id"] = "55555555-5555-5555-5555-555555555555",
                                        }),
                                    },
                                ],
                            }],
                        };
                });
            this.Layer.Setup(layer => layer.UpdateAsync(It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((UpdateRequest request, CancellationToken token) =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (this.gate)
                    {
                        this.OnUpdate?.Invoke(request);
                        var change = Assert.Single(request.Changes);
                        if (change.ConcurrencyTag != this.current.ConcurrencyTag)
                            return FailedConcurrencyUpdate(this.current);
                        var result = SuccessfulUpdate(this.current, change.Data!.Value, $"revision-{++this.revision}");
                        this.current = this.current with
                        {
                            Data = change.Data,
                            ConcurrencyTag = result.EntityResults.Single().ConcurrencyTag,
                        };
                        this.OnUpdated?.Invoke(request);
                        return result;
                    }
                });
            var client = new Mock<IChatClient>();
            client.Setup(value => value.GetService(It.IsAny<Type>(), It.IsAny<object?>()))
                .Returns((Type _, object? _) => this.FailStartup
                    ? throw new IOException("Replacement backend unavailable.")
                    : null);
            var services = new AgentServices { ChatClientOverride = client.Object };
            var persistence = new Mock<IAgentPersistenceStore>();
            persistence.Setup(value => value.StoreAsync(It.IsAny<StoreRequestAgent>(), It.IsAny<CancellationToken>()))
                .Returns((StoreRequestAgent request, CancellationToken token) => this.FailFinalPersistence
                    ? ValueTask.FromException(new IOException("Final framework persistence unavailable."))
                    : this.Store.StoreAsync(request, token));
            persistence.Setup(value => value.RestoreAsync(It.IsAny<RestoreRequest>(), It.IsAny<CancellationToken>()))
                .Returns((RestoreRequest request, CancellationToken token) => this.Store.RestoreAsync(request, token));
            persistence.Setup(value => value.ReadMessagesAsync(It.IsAny<ReadMessagesRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ReadMessagesRequest request, CancellationToken token) => this.Store.ReadMessagesAsync(request, token));
            persistence.Setup(value => value.ReadSubAgentChildIdsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string sessionId, CancellationToken token) => this.Store.ReadSubAgentChildIdsAsync(sessionId, token));
            this.ChatFactory = new AgentChatFactory(
                persistence.Object, services, foregroundScheduler ?? TaskScheduler.Default);
            this.Table = new RunningAgentChatTable(this.ChatFactory);
            this.Registry = new RemoteAgentSessionRuntimeRegistry(this.Time);
            var runtimeFactory = new DataAccessAgentSessionRuntimeHostFactory(
                this.Layer.Object, this.Table, new AgentSessionRuntimeContextFactory(null), this.Time, services);
            this.Host = new RemoteAgentSessionHost(
                new AgentSessionAttachAuthorizer(this.Layer.Object, this.Registry), this.Registry, runtimeFactory);
            var peer = new TransportPeerIdentity
            {
                AuthenticationScheme = "local-workspace-session",
                StablePeerId = "local",
                UserEntityId = "55555555-5555-5555-5555-555555555555",
                UserComputerProfileEntityId = Owner,
            };
            this.Table.ConfigureOwnerRuntime(this.Host, peer);
            var listener = new AgentSessionTransportListener(this.Host, this.peerIdentities);
            this.listeners.Register(dropHandoff
                ? new RestartHandoffDroppingListener(listener, this.peerIdentities)
                : listener);
            this.localTransportFactory = new LocalTransportFactory(
                this.listeners, channel => this.peerIdentities.SetIdentity(channel, peer));
        }

        internal async Task<RemoteAgentChat> AttachRemoteAsync()
        {
            var transport = await this.localTransportFactory.ConnectToAsync(
                JsonSerializer.SerializeToElement(new { type = "local" }), TestContext.Current.CancellationToken);
            return await RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
            {
                Client = new RemoteAgentSessionClient(transport!, this.Time),
                ForegroundScheduler = TaskScheduler.Default,
                OpenRequest = new AgentSessionOpenRequest
                {
                    ProtocolVersion = 1,
                    AgentSessionId = "session",
                    ExpectedOwningProfileEntityId = Owner,
                    ExpectedOwnershipGeneration = 3,
                    OpenIntent = AgentSessionOpenIntent.Attach,
                    AttachmentToken = Guid.NewGuid().ToString("N"),
                    Capabilities = [],
                },
            }, TestContext.Current.CancellationToken);
        }

        internal Task<RunningAgentChatLease> AcquireAsync()
            => this.Table.AcquireAsync(new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("session"),
                AgentSessionEntity = this.current.Data,
            }, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            this.OnUpdate = null;
            this.OnUpdated = null;
            this.FailFinalPersistence = false;
            await this.Registry.DisposeAsync();
            await this.ChatFactory.DisposeAsync();
        }

        private sealed class RestartHandoffDroppingListener(
            ITransportListener inner, TransportPeerIdentityProvider identities) : ITransportListener
        {
            private readonly HashSet<string> attachments = new(StringComparer.Ordinal);

            public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            {
                if (request.GetProperty("type").GetString() == "attach-agent-session")
                {
                    var open = AgentSessionProtocolCodec.DeserializeOpen(request);
                    bool firstAttachment;
                    lock (this.attachments)
                        firstAttachment = this.attachments.Add(open.AttachmentToken);
                    if (firstAttachment)
                    {
                        var filtered = new HandoffDroppingChannel(channel);
                        identities.SetIdentity(filtered, identities.GetRequiredIdentity(channel));
                        channel = filtered;
                    }
                }
                return inner.OnChannelOpenAsync(request, channel, ct);
            }

            public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
                => inner.OnStreamOpenAsync(request, stream, ct);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class HandoffDroppingChannel(IMessageChannel inner) : IMessageChannel
        {
            public ChannelReader<JsonElement> Reader => inner.Reader;
            public ChannelWriter<JsonElement> Writer { get; } = new HandoffDroppingWriter(inner.Writer);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class HandoffDroppingWriter(ChannelWriter<JsonElement> inner) : ChannelWriter<JsonElement>
        {
            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
            public override bool TryWrite(JsonElement item)
                => item.GetProperty("type").GetString() == "session-restarting" || inner.TryWrite(item);
            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
                => inner.WaitToWriteAsync(cancellationToken);
        }
    }
}
