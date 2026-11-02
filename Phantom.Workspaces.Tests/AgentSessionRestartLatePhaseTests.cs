using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_UnpublishedCleanupDeadline_ReleasesLocalGateAndStillRetiresWrapper(
        bool failAuthorityRefresh)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AgentSessions.RemoteAgentSessionLease? unpublished = null;
        environment.BeforePublication = replacement =>
        {
            unpublished = replacement;
            Assert.IsType<AgentChat>(replacement.Chat).RegisterOwnedResource(
                new GatedRestartCleanupResource(entered, release.Task));
            if (failAuthorityRefresh)
                environment.BeforeQueryAsync = () => Task.FromException(new IOException("Authority query failed."));
            throw new IOException("Publication failed.");
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            Assert.NotNull(await Record.ExceptionAsync(() => restarting.WaitAsync(TestContext.Current.CancellationToken)));
            environment.BeforeQueryAsync = null;
            Assert.True(unpublished!.IsFenced);
            var gates = (Dictionary<AgentSessionId, SemaphoreSlim>)typeof(Services.RunningAgentChatTable)
                .GetField("localAcquisitionGates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(environment.Table)!;
            var gate = gates[new AgentSessionId("session")];
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            gate.Release();
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken))!.State);
            await Assert.ThrowsAnyAsync<Exception>(() => environment.AcquireAsync());
        }
        finally
        {
            environment.BeforeQueryAsync = null;
            release.TrySetResult();
            if (unpublished?.Chat is AgentChat chat)
                await chat.TerminateAsync(TestContext.Current.CancellationToken);
        }
        Assert.Empty(environment.Table.RunningSessions);
    }

    private sealed class GatedRestartCleanupResource(TaskCompletionSource entered, Task release) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            entered.TrySetResult();
            await release;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartSession_StartupDeadline_BlocksLateInitializationAndReleasesAcquisition(
        bool recoveredHost, bool disconnectCaller)
    {
        var environment = new RestartEnvironment();
        try
        {
            await using var viewer = await environment.AcquireAsync();
            var request = new AgentSessionOwnerRestartRequest
            {
                CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
            };
            if (recoveredHost)
            {
                environment.FailStartup = true;
                Assert.Equal(AgentSessionOwnerRestartState.Stopped,
                    (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
                var store = environment.Store;
                var persisted = environment.Persisted;
                await viewer.DisposeAsync();
                await environment.DisposeAsync();
                environment = new RestartEnvironment(store, persisted);
                environment.Time.Advance(TimeSpan.FromMinutes(2));
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BeforeRestoreAsync = async () =>
            {
                entered.TrySetResult();
                await release.Task;
                finished.TrySetResult();
            };
            using var caller = new CancellationTokenSource();
            var restarting = environment.Host.RestartAsync(RestartPeer(), request, caller.Token);
            try
            {
                await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
                if (disconnectCaller)
                    caller.Cancel();
                environment.Time.Advance(TimeSpan.FromSeconds(15));
                Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                    (await restarting.WaitAsync(TestContext.Current.CancellationToken)).State);
                Assert.Empty(environment.Table.RunningSessions);
                Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                    (await environment.Host.RestartAsync(RestartPeer(), request, TestContext.Current.CancellationToken)).State);
                await Assert.ThrowsAnyAsync<Exception>(() => environment.AcquireAsync());
            }
            finally { release.TrySetResult(); }
            await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
            environment.BeforeRestoreAsync = null;
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await environment.Host.RestartAsync(RestartPeer(), request, TestContext.Current.CancellationToken)).State);
        }
        finally { await environment.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_LocalCompositionDeadline_DoesNotRebindOnLateCompletion(bool closeTab)
    {
        await using var environment = new RestartEnvironment(
            foregroundScheduler: TaskScheduler.FromCurrentSynchronizationContext());
        var viewer = await environment.AcquireAsync();
        using var logger = new ObservableLoggerFactory();
        var original = new AgentViewModel(viewer.AgentChat, "original", "", logger, TaskScheduler.FromCurrentSynchronizationContext());
        await using var tab = new AgentSessionWorkspaceTabViewModel { Id = "late-view", Title = "Late view", AgentSessionId = "session" };
        tab.SetLease(viewer);
        tab.SetReady(original, logger);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.ComposeRuntimeReplacement = async chat =>
        {
            entered.TrySetResult();
            await release.Task;
            return new AgentViewModel(chat, "late", "", logger, TaskScheduler.FromCurrentSynchronizationContext());
        };
        var productionReattach = viewer.ReattachAsync!;
        viewer.ReattachAsync = async replacement =>
        {
            try { await productionReattach(replacement); }
            finally { finished.TrySetResult(); }
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (closeTab)
                await tab.DisposeAsync();
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await restarting.WaitAsync(TestContext.Current.CancellationToken)).State);
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (!closeTab)
            Assert.Same(original, tab.Agent);
        Assert.Empty(environment.Table.RunningSessions);
    }
}
