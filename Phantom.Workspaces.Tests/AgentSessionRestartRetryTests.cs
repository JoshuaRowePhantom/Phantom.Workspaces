using Microsoft.Extensions.AI;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Fact]
    public async Task RestartSession_BlockedDiagnosticBackpressure_HasOwnerDeadlineAndFencesRuntime()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var runtime = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        await using var channel = new RestartBlockedChannel("session-restart-stopped");
        await using var attachment = runtime.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "blocked-stopped", Channel = channel,
        });
        environment.FailFinalPersistence = true;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        await channel.Blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(restarting.IsCompleted);
        await Assert.IsType<RestartTestTimeProvider>(environment.Time)
            .WaitForPendingTimersAsync(TimeSpan.FromSeconds(5));
        environment.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await restarting.WaitAsync(TestContext.Current.CancellationToken)).State);
        await channel.WriteCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(runtime.IsFenced);
        Assert.Empty(environment.Table.RunningSessions);
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken))!.State);
    }

    [Fact]
    public async Task RestartSession_BlockedDiagnostic_QuiescenceDeadlineCannotAuthorizeLateReplacement()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownership = new AgentSessionOwnershipLease(environment.Time,
            async _ =>
            {
                entered.TrySetResult();
                await finish.Task;
                return new AgentSessionOwnershipLeasePeriod(environment.Time.GetUtcNow(),
                    environment.Time.GetUtcNow().AddMinutes(1));
            }, _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
        ownership.Start(new AgentSessionOwnershipLeasePeriod(environment.Time.GetUtcNow(),
            environment.Time.GetUtcNow().AddMinutes(1)));
        var lifetime = new Mock<IAsyncDisposable>();
        lifetime.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        await using var runtime = new RemoteAgentSessionLease("isolated", 3, viewer.RestartIdentity!.RuntimeEpoch,
            viewer.AgentChat, false, () => throw new InvalidOperationException(),
            timeProvider: environment.Time, ownershipLease: ownership, runtimeLifetime: lifetime.Object,
            terminateRuntimeAsync: () => Task.CompletedTask);
        environment.Time.Advance(TimeSpan.FromSeconds(10));
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var publishing = runtime.PublishRestartStoppedAsync(
                new SessionRestartStoppedEvent { Message = "Cleanup is blocked." }, blockRuntime: true).AsTask();
            Assert.False(publishing.IsCompleted);
            environment.Time.Advance(TimeSpan.FromSeconds(5));
            await publishing.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(runtime.IsFenced);
        }
        finally { finish.TrySetResult(); }
        await ownership.DisposeAsync();
        Assert.True(runtime.IsFenced);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_UnpublishedCleanupDeadline_RemainsBlockedAfterLateResourceCompletion(bool disconnectCaller)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        await using var remote = await environment.AttachRemoteAsync();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RuntimeStateChanged += (_, _) =>
        {
            if (remote.RestartClient.RestartCommandState.Result?.State == AgentSessionOwnerRestartState.Blocked)
                blocked.TrySetResult();
        };
        using var caller = new CancellationTokenSource();
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creations = 0;
        environment.BeforePublication = replacement =>
        {
            creations++;
            var resource = new Mock<IAsyncDisposable>();
            resource.Setup(value => value.DisposeAsync()).Returns(async () =>
            {
                disposing.TrySetResult();
                await release.Task;
                disposed.TrySetResult();
            });
            Assert.IsType<AgentChat>(replacement.Chat).RegisterOwnedResource(resource.Object);
            throw new IOException("Publication failed.");
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, caller.Token);
        try
        {
            await disposing.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (disconnectCaller) caller.Cancel();
            await Assert.IsType<RestartTestTimeProvider>(environment.Time)
                .WaitForPendingTimersAsync(TimeSpan.FromSeconds(15), 4);
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarting);
            await blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(request, remote.RestartClient.RestartCommandState.Pending);
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken))!.State);
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        }
        finally { release.TrySetResult(); }
        await disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        Assert.Equal(1, creations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RestartSession_RetiredRemoteNonRequester_ObservesStoppedReasonAndSameOperationRetry(bool retryFromRemote, bool loseHandoff)
    {
        await using var environment = new RestartEnvironment(dropHandoff: loseHandoff);
        await using var viewer = await environment.AcquireAsync();
        await using var remote = await environment.AttachRemoteAsync();
        var old = Assert.IsType<AgentChat>(viewer.AgentChat);
        var queue = old.QueueManager.CreateInputQueue("Retained retry", AgentInputQueueImmediacy.Held);
        old.EnqueueUserMessage("never replay", queue);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RuntimeStateChanged += (_, _) =>
        {
            if (!remote.IsConnected)
                disconnected.TrySetResult();
            if (remote.IsTerminal && remote.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(content => content.Text.Contains("Retry this restart operation", StringComparison.Ordinal))))
                stopped.TrySetResult();
            if (remote.IsConnected && remote.RestartClient.RestartIdentity?.RuntimeEpoch != viewer.RestartIdentity!.RuntimeEpoch)
                replaced.TrySetResult();
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        environment.FailStartup = true;
        var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Stopped, result.State);
        await disconnected.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stopped.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(request, remote.RestartClient.RestartCommandState.Pending);
        Assert.Equal(result, remote.RestartClient.RestartCommandState.Result);
        Assert.Empty(environment.Table.RunningSessions);
        environment.FailStartup = false;
        var retry = retryFromRemote
            ? await remote.RestartClient.RestartAsync(remote.RestartClient.RestartCommandState.Pending!, TestContext.Current.CancellationToken)
            : await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, retry.State);
        Assert.Equal(result.ReplacementRuntime, retry.ReplacementRuntime);
        await remote.ReconnectNowAsync(TestContext.Current.CancellationToken);
        await replaced.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(remote.IsTerminal);
        Assert.Single(remote.InputQueues.Snapshot.Queues.Single(item => item.QueueId == queue.Queue.QueueId).Items);
        Assert.Single(environment.Table.RunningSessions);
        Assert.Equal(retry, await remote.RestartClient.GetRestartStatusAsync(request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartSession_OwnerCleanupDeadline_BlocksLateCleanupAndNeverStartsReplacement(
        bool blockPersistence, bool disconnectCaller)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var old = Assert.IsType<AgentChat>(viewer.AgentChat);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistenceCalls = 0;
        environment.FinalPersistence = async (_, token) =>
        {
            Interlocked.Increment(ref persistenceCalls);
            if (blockPersistence)
            {
                entered.TrySetResult();
                try
                {
                    await finish.Task;
                    token.ThrowIfCancellationRequested();
                }
                finally { finished.TrySetResult(); }
            }
        };
        if (!blockPersistence)
        {
            var resource = new Mock<IAsyncDisposable>();
            resource.Setup(value => value.DisposeAsync()).Returns(async () =>
            {
                entered.TrySetResult();
                await finish.Task;
                finished.TrySetResult();
            });
            old.RegisterOwnedResource(resource.Object);
        }
        using var caller = new CancellationTokenSource();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (disconnectCaller)
                caller.Cancel();
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            var result = await restarting.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AgentSessionOwnerRestartState.Blocked, result.State);
            Assert.Empty(environment.Table.RunningSessions);
            Assert.Equal(result, await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken));
            Assert.Equal(result, await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
            Assert.Equal(blockPersistence ? 1 : 0, persistenceCalls);
            await Assert.ThrowsAsync<Services.AgentSessions.AgentSessionTakeoverBlockedException>(() =>
                environment.Host.TakeOverAsync(RestartPeer(), new AgentSessionTakeoverRequest
                {
                    AgentSessionId = "session", ExpectedOwningProfileEntityId = Owner,
                    ExpectedOwnershipGeneration = 3, NewOwningProfileEntityId = Owner,
                    CorrelationId = Guid.NewGuid(),
                }, TestContext.Current.CancellationToken));
        }
        finally
        {
            finish.TrySetResult();
        }
        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await old.DisposeAsync();
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(blockPersistence ? 1 : 0, persistenceCalls);
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        await using var recovered = new RestartEnvironment(environment.Store, environment.Persisted);
        recovered.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await recovered.Host.RestartAsync(RestartPeer(), request, TestContext.Current.CancellationToken)).State);
        Assert.Empty(recovered.Table.RunningSessions);
    }
}
