using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Services.AgentSessions;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionOwnershipLeaseTests
{
    [Fact]
    public async Task OwnershipLease_RenewalEveryTenSeconds_ExtendsThirtySecondExpiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var renewed = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = new AgentSessionOwnershipLease(
            time,
            _ =>
            {
                var expiry = time.GetUtcNow() + TimeSpan.FromSeconds(30);
                renewed.TrySetResult(expiry);
                return ValueTask.FromResult<DateTimeOffset?>(expiry);
            },
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask);
        lease.Start(time.GetUtcNow() + TimeSpan.FromSeconds(30));

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), await renewed.Task);
    }

    [Fact]
    public async Task OwnershipLease_RenewalUncertain_FencesBeforeSafetyDeadline()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var fenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = new AgentSessionOwnershipLease(
            time,
            _ => ValueTask.FromResult<DateTimeOffset?>(null),
            _ => { fenced.TrySetResult(); return ValueTask.CompletedTask; },
            _ => ValueTask.CompletedTask);
        lease.Start(time.GetUtcNow() + TimeSpan.FromSeconds(14));

        time.Advance(TimeSpan.FromSeconds(10));

        await fenced.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OwnershipLease_RenewalBlockedAtSafetyDeadline_FencesDeterministically()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var renewalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRenewed = new TaskCompletionSource<DateTimeOffset?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fenced = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = new AgentSessionOwnershipLease(
            time,
            async ct =>
            {
                renewalStarted.SetResult();
                return await neverRenewed.Task.WaitAsync(ct);
            },
            _ =>
            {
                fenced.SetResult();
                return ValueTask.CompletedTask;
            },
            _ => ValueTask.CompletedTask);
        lease.Start(time.GetUtcNow() + TimeSpan.FromSeconds(16));

        time.Advance(TimeSpan.FromSeconds(10));
        await renewalStarted.Task;
        time.Advance(TimeSpan.FromSeconds(1));

        await fenced.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OwnershipLease_QuiesceAsync_CancelsAndAwaitsInFlightRenewal()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var renewalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        await using var lease = new AgentSessionOwnershipLease(
            time,
            async ct =>
            {
                renewalStarted.SetResult();
                try
                {
                    await Task.CompletedTask.WaitAsync(ct);
                    await new TaskCompletionSource().Task.WaitAsync(ct);
                    return null;
                }
                finally
                {
                    renewalExited.SetResult();
                }
            },
            _ => ValueTask.CompletedTask,
            _ =>
            {
                released = true;
                return ValueTask.CompletedTask;
            });
        lease.Start(time.GetUtcNow() + TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(10));
        await renewalStarted.Task;

        await lease.QuiesceAsync();

        Assert.True(renewalExited.Task.IsCompleted);
        Assert.False(released);
    }
}
