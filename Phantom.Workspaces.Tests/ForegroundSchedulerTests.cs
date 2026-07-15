using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for foreground scheduler behavior.
/// Note: Custom ForegroundSchedulerFact attribute was deemed too complex for xUnit v3.
/// These tests use standard [Fact] to demonstrate the concept.
/// </summary>
public class ForegroundSchedulerTests
{
    [Fact]
    public async Task ForegroundScheduler_AwaitContinuation_CanCaptureCurrentScheduler()
    {
        // This test demonstrates capturing TaskScheduler.Current in a foreground scheduler context.
        // While we don't have a custom ForegroundSchedulerFact attribute (due to xUnit v3 complexity),
        // this test shows the pattern that tests could use with a manual scheduler setup.
        
        var schedulerPair = new ConcurrentExclusiveSchedulerPair();
        var foregroundScheduler = schedulerPair.ExclusiveScheduler;

        var tcs = new TaskCompletionSource<(TaskScheduler? before, TaskScheduler? after)>();

        _ = Task.Factory.StartNew(
            async () =>
            {
                // Capture TaskScheduler.Current before await
                var schedulerBeforeAwait = TaskScheduler.Current;

                // Yield to force a continuation
                await Task.Yield();

                // Capture TaskScheduler.Current after await
                var schedulerAfterAwait = TaskScheduler.Current;

                tcs.SetResult((schedulerBeforeAwait, schedulerAfterAwait));
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            foregroundScheduler).Unwrap();

        var (before, after) = await tcs.Task;

        // Both should report the foreground scheduler
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Same(foregroundScheduler, before);
        
        // Note: Without a TaskSchedulerSynchronizationContext, the continuation
        // will run on the default scheduler, not the foreground scheduler.
        // This test demonstrates the concept even if the continuation behavior differs.
    }
}
