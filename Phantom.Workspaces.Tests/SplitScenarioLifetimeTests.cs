using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.ProcessAcceptance;

namespace Phantom.Workspaces.Tests;

public sealed class SplitScenarioLifetimeTests
{
    [Fact]
    public async Task PreparationDoesNotConsumeOperationOrCleanupBudget()
    {
        var clock = new FakeTimeProvider();
        using (var preparation = new SplitScenarioPhase("preparation", TimeSpan.FromSeconds(90), default, clock))
        {
            clock.Advance(TimeSpan.FromSeconds(89));
            await preparation.RunAsync(token => Task.CompletedTask);
        }
        using (var operation = new SplitScenarioPhase("operation", TimeSpan.FromSeconds(90), default, clock))
        {
            clock.Advance(TimeSpan.FromSeconds(89));
            Assert.False(operation.Token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() => operation.RunAsync(
                token => Task.FromCanceled(token)));
        }
        using var cleanup = new SplitScenarioPhase("cleanup", TimeSpan.FromSeconds(30), default, clock);
        Assert.False(cleanup.Token.IsCancellationRequested);
        await cleanup.RunAsync(token => Task.CompletedTask);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("role-startup")]
    [InlineData("operation")]
    [InlineData("caller-exit")]
    [InlineData("worker-stop")]
    [InlineData("worker-exit")]
    [InlineData("cleanup")]
    [InlineData("stderr-drain")]
    public async Task PhaseDeadlineNamesOwnerAndCancelsPendingWork(string name)
    {
        var clock = new FakeTimeProvider();
        using var phase = new SplitScenarioPhase(name, TimeSpan.FromSeconds(30), default, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = phase.RunAsync(async token =>
        {
            entered.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(token);
        });
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Contains(name, failure.Message);
    }

    [Fact]
    public async Task ExternalCancellationRemainsAuthoritativeButDoesNotCancelCleanup()
    {
        var clock = new FakeTimeProvider();
        using var external = new CancellationTokenSource();
        using var operation = new SplitScenarioPhase("operation", TimeSpan.FromSeconds(90), external.Token, clock);
        external.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.RunAsync(token => Task.FromCanceled(token)));
        using var cleanup = new SplitScenarioPhase("cleanup", TimeSpan.FromSeconds(30), default, clock);
        await cleanup.RunAsync(token => Task.CompletedTask);
    }

    [Fact]
    public async Task CleanupAttemptsEveryOwnerAndPreservesPrimaryAndCleanupFailures()
    {
        var clock = new FakeTimeProvider();
        var owners = new SplitScenarioResources(TimeSpan.FromSeconds(30), clock);
        var disposed = new List<int>();
        owners.Add(new Owner(() => { disposed.Add(1); return ValueTask.CompletedTask; }));
        owners.Add(new Owner(() => { disposed.Add(2); throw new IOException("cleanup"); }));
        var primary = new InvalidOperationException("operation");
        var failure = await Assert.ThrowsAsync<AggregateException>(() => owners.CompleteAsync(primary));
        Assert.Equal([2, 1], disposed);
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.IsType<IOException>(failure.InnerExceptions[1]);
    }

    [Fact]
    public async Task HungCleanupIsBoundedAndRemainingOwnersStillDispose()
    {
        var clock = new FakeTimeProvider();
        var owners = new SplitScenarioResources(TimeSpan.FromSeconds(30), clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        owners.Add(new Owner(() => { disposed = true; return ValueTask.CompletedTask; }));
        owners.Add(new Owner(() => { entered.SetResult(); return new ValueTask(release.Task); }));
        var cleanup = owners.CompleteAsync();
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => cleanup);
        Assert.IsType<TimeoutException>(Assert.Single(failure.InnerExceptions));
        Assert.True(disposed);
        release.SetResult();
    }

    [Fact]
    public async Task SuccessfulCleanupDoesNotReplacePrimaryFailure()
    {
        var owners = new SplitScenarioResources();
        owners.Add(new Owner(() => ValueTask.CompletedTask));
        var primary = new IOException("primary");
        var failure = await Assert.ThrowsAsync<IOException>(() => owners.CompleteAsync(primary));
        Assert.Same(primary, failure);
    }

    [Fact]
    public async Task ResultAtBoundaryDoesNotExtendPhase()
    {
        var clock = new FakeTimeProvider();
        using var phase = new SplitScenarioPhase("operation", TimeSpan.FromSeconds(30), default, clock);
        clock.Advance(TimeSpan.FromSeconds(29));
        await phase.RunAsync(token => Task.CompletedTask);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => phase.RunAsync(token => Task.CompletedTask));
    }

    [Fact]
    public async Task NonCooperativeAwaitCannotHoldPhaseOpenPastItsDeadline()
    {
        var clock = new FakeTimeProvider();
        using var phase = new SplitScenarioPhase("preparation", TimeSpan.FromSeconds(30), default, clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = phase.RunAsync(_ => release.Task);
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        release.SetResult();
    }

    [Fact]
    public async Task SuccessfulResultFollowedByExpiredOperationStillAllowsGatedCleanup()
    {
        var clock = new FakeTimeProvider();
        using var operation = new SplitScenarioPhase("operation", TimeSpan.FromSeconds(90), default, clock);
        await operation.RunAsync(_ => Task.CompletedTask);
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.True(operation.Token.IsCancellationRequested);
        var resources = new SplitScenarioResources(TimeSpan.FromSeconds(30), clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resources.Add(new Owner(() =>
        {
            entered.SetResult();
            return new ValueTask(release.Task);
        }));
        var cleanup = resources.CompleteAsync();
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(cleanup.IsCompleted);
        release.SetResult();
        await cleanup;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureAndDeadlineRemainClassifiedAsCleanupAfterAggregation(bool timeout)
    {
        var clock = new FakeTimeProvider();
        var operationLines = new List<string>();
        var cleanupLines = new List<string>();
        var operation = new ProcessStageDiagnostics(operationLines.Add, clock);
        var cleanup = new ProcessStageDiagnostics(cleanupLines.Add, clock);
        var resources = new SplitScenarioResources(TimeSpan.FromSeconds(30), clock, cleanup);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resources.Add(new DiagnosticAsyncLifetime(new Owner(() =>
        {
            entered.SetResult();
            return timeout ? new ValueTask(release.Task) : ValueTask.FromException(new IOException("cleanup"));
        }), cleanup, "window-shutdown"));
        operation.Report("result");
        operation.Complete();
        var pending = resources.CompleteAsync();
        await entered.Task;
        if (timeout)
            clock.Advance(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => pending);
        operation.CaptureFailure(failure, cleanup);
        Assert.False(operation.HasFailure);
        Assert.True(cleanup.HasFailure);
        Assert.Contains(cleanupLines, line => line.Contains("failed window-shutdown"));
        release.SetResult();
    }

    [Fact]
    public void MixedAggregateFailureIsNotMistakenForCleanupOnly()
    {
        var operation = new ProcessStageDiagnostics(_ => { });
        var cleanup = new ProcessStageDiagnostics(_ => { });
        var cleanupFailure = new IOException();
        cleanup.Fail(cleanupFailure);
        operation.Report("operation");
        operation.CaptureFailure(new AggregateException(new InvalidOperationException(), cleanupFailure), cleanup);
        Assert.True(operation.HasFailure);
    }

    private sealed class Owner(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
