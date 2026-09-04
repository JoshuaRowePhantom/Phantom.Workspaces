using System;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionWorkspaceTabViewModelTests
{
    [Fact]
    public async Task AgentSessionWorkspaceTabViewModel_DisposeAsyncDuringLoading_LeaseAcquiredLaterIsStillReleased()
    {
        // #1340: the tab is disposed while still in its loading state (no agent yet), so
        // DisposeAsync takes the else-branch and disposes leaseDisposables. A racing background
        // initialization (OpenAgentSessionShortcutHandler.InitializeTabInBackgroundAsync) can then
        // still acquire a lease and hand it to SetLease AFTER disposal. The AsyncDisposableCollection
        // post-dispose Add contract must dispose that late lease immediately so the
        // RunningAgentChatLease is never leaked.
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-race",
            Title = "Agent Race",
        };

        // Dispose while loading (agent is null): the lease acquired below arrives post-dispose.
        await tab.DisposeAsync();

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new RunningAgentChatLease(
            new AgentSessionId("session-race-1340"),
            null!,
            () =>
            {
                released.TrySetResult();
                return ValueTask.CompletedTask;
            });

        // The racing background init acquires the lease after DisposeAsync.
        tab.SetLease(lease);

        // The post-dispose Add must dispose the lease immediately (asynchronously).
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(released.Task.IsCompletedSuccessfully);
    }
}
