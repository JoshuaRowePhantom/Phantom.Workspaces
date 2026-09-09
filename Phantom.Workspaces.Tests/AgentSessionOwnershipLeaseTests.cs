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
}
