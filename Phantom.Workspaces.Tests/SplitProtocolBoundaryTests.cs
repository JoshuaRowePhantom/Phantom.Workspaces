using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.ProcessAcceptance;

namespace Phantom.Workspaces.Tests;

public sealed partial class ProcessIsolatedSplitAcceptanceTests
{
    [Theory]
    [InlineData("result", 29, true)]
    [InlineData("result", 30, false)]
    [InlineData("result", 31, false)]
    [InlineData("caller-exit", 29, true)]
    [InlineData("caller-exit", 30, false)]
    [InlineData("caller-exit", 31, false)]
    [InlineData("stop", 29, true)]
    [InlineData("stop", 30, false)]
    [InlineData("stop", 31, false)]
    [InlineData("worker-exit", 29, true)]
    [InlineData("worker-exit", 30, false)]
    [InlineData("worker-exit", 31, false)]
    public async Task ProtocolBoundariesUseOneDeadlineAndCleanRealOwnedRole(
        string boundary, int seconds, bool succeeds)
    {
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Gate(CancellationToken token)
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        }
        var hooks = new RoleProcessHooks
        {
            AfterLine = (line, token) => (boundary == "result" && line == "RESULT probe")
                || (boundary == "stop" && line == "STOP accepted") ? Gate(token) : Task.CompletedTask,
        };
        var role = new RoleProcess(clock, hooks, "lifetime-probe",
            boundary is "caller-exit" or "worker-exit" ? "gated-cleanup" : "protocol");
        var owners = new SplitScenarioResources(timeProvider: clock);
        var remainingDisposed = false;
        owners.Add(new CallbackOwner(() => { remainingDisposed = true; return ValueTask.CompletedTask; }));
        owners.Add(role);
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("READY probe", await role.NextLineAsync(cancellation));
        var protocol = new SplitScenarioProtocol(clock, cancellation, TimeSpan.FromSeconds(30));
        Task pending;
        if (boundary == "result")
            pending = protocol.OperationAsync(role, "RESULT probe");
        else if (boundary == "stop")
            pending = protocol.StopWorkerAsync(role);
        else
        {
            await role.SendAsync("STOP", cancellation);
            Assert.Equal("STOP accepted", await role.NextLineAsync(cancellation));
            pending = boundary == "caller-exit"
                ? protocol.CallerExitAsync(role) : protocol.WorkerExitAsync(role);
            entered.SetResult();
        }
        await entered.Task.WaitAsync(cancellation);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        release.SetResult();
        if (succeeds)
        {
            if (boundary is "caller-exit" or "worker-exit")
                await role.SendAsync("RELEASE", cancellation);
            await pending;
        }
        else
        {
            Assert.Contains(boundary == "result" ? "operation" : boundary == "stop" ? "worker-stop" : boundary,
                (await Assert.ThrowsAsync<TimeoutException>(() => pending)).Message);
            if (boundary is "caller-exit" or "worker-exit")
            {
                await role.SendAsync("RELEASE", cancellation);
                await role.WaitForTerminationAsync(cancellation);
            }
        }
        await owners.CompleteAsync();
        Assert.True(role.PipesClosed);
        Assert.True(role.ProcessExited);
        Assert.True(remainingDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncidentalProtocolOutputDoesNotRenewResultDeadlineOrHideActiveCancellation(bool cancel)
    {
        var clock = new FakeTimeProvider();
        using var external = new CancellationTokenSource();
        var incidental = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var role = new RoleProcess(clock, new RoleProcessHooks
        {
            BeforeOutputRead = token =>
            {
                if (Interlocked.Increment(ref reads) != 3)
                    return Task.CompletedTask;
                readEntered.SetResult();
                return readRelease.Task.WaitAsync(token);
            },
            AfterLine = (line, _) =>
            {
                if (line == "INFO waiting")
                    incidental.SetResult();
                return Task.CompletedTask;
            },
        }, "lifetime-probe", "pending-result");
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var protocol = new SplitScenarioProtocol(clock, external.Token, TimeSpan.FromSeconds(30));
        var pending = protocol.OperationAsync(role, "RESULT probe");
        await incidental.Task.WaitAsync(TestContext.Current.CancellationToken);
        await readEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (cancel)
        {
            external.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var stop = protocol.StopWorkerAsync(role);
            Assert.False(stop.IsCompleted);
            readRelease.SetResult();
            await stop;
            await protocol.WorkerExitAsync(role);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
            readRelease.SetResult();
        }
        await role.DisposeAsync();
        Assert.True(role.ProcessExited);
        Assert.True(role.PipesClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPreparationIsTerminatedAndReapedBeforeTimeoutOrCancellationCompletes(bool cancel)
    {
        var clock = new FakeTimeProvider();
        using var external = new CancellationTokenSource();
        var role = new RoleProcess(clock, "lifetime-probe", "pending-preparation");
        var owners = new SplitScenarioResources(timeProvider: clock);
        owners.Add(role);
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var protocol = new SplitScenarioProtocol(clock, external.Token, TimeSpan.FromSeconds(30));
        var pending = protocol.PrepareAsync(role);
        if (cancel)
            external.Cancel();
        else
            clock.Advance(TimeSpan.FromSeconds(30));
        Exception failure = cancel
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending)
            : await Assert.ThrowsAsync<TimeoutException>(() => pending);
        var cleanup = owners.CompleteAsync(failure);
        await role.CleanupEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        var combined = await Assert.ThrowsAsync<AggregateException>(() => cleanup);
        Assert.Same(failure, combined.InnerExceptions[0]);
        Assert.True(role.WasTerminated);
        Assert.True(role.ProcessExited);
        Assert.True(role.PipesClosed);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("stderr-timeout")]
    [InlineData("stderr-fault")]
    [InlineData("stop-and-stderr-fault")]
    public async Task ActualRolePipeFailurePreservesPrimaryAndDisposesRemainingOwners(string fault)
    {
        var clock = new FakeTimeProvider();
        var blockedError = fault == "stderr-timeout";
        var stopFailure = fault.StartsWith("stop", StringComparison.Ordinal);
        var errorFailure = fault.EndsWith("fault", StringComparison.Ordinal);
        var errorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var role = new RoleProcess(clock, new RoleProcessHooks
        {
            BeforeSend = (_, _) => stopFailure
                ? Task.FromException(new IOException("controlled STOP pipe failure")) : Task.CompletedTask,
            ReadError = blockedError ? async token =>
            {
                errorEntered.TrySetResult();
                try { await new TaskCompletionSource().Task.WaitAsync(token); }
                finally { errorCancelled.SetResult(); }
                return null;
            } : errorFailure ? _ => ValueTask.FromException<string?>(new IOException("controlled stderr fault")) : null,
        }, "lifetime-probe", "protocol");
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var remaining = false;
        var owners = new SplitScenarioResources(timeProvider: clock);
        owners.Add(new CallbackOwner(() => { remaining = true; return ValueTask.CompletedTask; }));
        owners.Add(role);
        var primary = new InvalidOperationException("operation failed first");
        var pending = owners.CompleteAsync(primary);
        if (blockedError)
        {
            await errorEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await role.DrainEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        var failure = await Assert.ThrowsAsync<AggregateException>(() => pending);
        Assert.Same(primary, failure.InnerExceptions[0]);
        if (blockedError)
        {
            Assert.Contains("stderr-drain", Assert.IsType<TimeoutException>(failure.InnerExceptions[1]).Message);
            await errorCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        else if (fault == "stop-and-stderr-fault")
        {
            var errors = Assert.IsType<AggregateException>(failure.InnerExceptions[1]).Flatten().InnerExceptions;
            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, error => error.Message.Contains("STOP", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Message.Contains("stderr", StringComparison.Ordinal));
        }
        else if (stopFailure)
            Assert.Contains("STOP", Assert.IsType<IOException>(failure.InnerExceptions[1]).Message);
        else
            Assert.Contains("stderr", Assert.IsType<IOException>(failure.InnerExceptions[1]).Message);
        Assert.True(role.ProcessExited);
        Assert.True(role.PipesClosed);
        Assert.True(remaining);
    }

    [Fact]
    public async Task ResultThenExpiredOperationStillAllowsRealWorkerGatedCleanup()
    {
        var clock = new FakeTimeProvider();
        var role = new RoleProcess(clock, "lifetime-probe", "gated-cleanup");
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var protocol = new SplitScenarioProtocol(clock, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await protocol.OperationAsync(role, "RESULT probe");
        clock.Advance(TimeSpan.FromSeconds(30));
        await protocol.StopWorkerAsync(role);
        var exit = protocol.WorkerExitAsync(role);
        Assert.False(exit.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(29));
        await role.SendAsync("RELEASE", TestContext.Current.CancellationToken);
        await exit;
        await role.DisposeAsync();
        Assert.True(role.ProcessExited);
        Assert.True(role.PipesClosed);
    }

    [Fact]
    public async Task OwnedOutputPumpFaultIsObservedWithoutWaitingForStderrAndStillCleansTheRole()
    {
        var reads = 0;
        var role = new RoleProcess(TimeProvider.System, new RoleProcessHooks
        {
            BeforeOutputRead = _ => Interlocked.Increment(ref reads) == 2
                ? Task.FromException(new IOException("controlled stdout failure")) : Task.CompletedTask,
        }, "lifetime-probe", "protocol");
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("READY probe", await role.NextLineAsync(cancellation));
        var primary = await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(
            () => role.NextLineAsync(cancellation));
        Assert.IsType<IOException>(primary.InnerException);
        var owners = new SplitScenarioResources();
        owners.Add(role);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => owners.CompleteAsync(primary));
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.Contains("stdout", Assert.IsType<IOException>(failure.InnerExceptions[1]).Message);
        Assert.True(role.ProcessExited && role.PipesClosed);
    }

    [Fact]
    public async Task WorkerDisposalPastItsIndependentDeadlineIsTerminatedWithTheOriginalExitFailure()
    {
        var clock = new FakeTimeProvider();
        var role = new RoleProcess(clock, "lifetime-probe", "hang-cleanup");
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var protocol = new SplitScenarioProtocol(clock, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30));
        await protocol.StopWorkerAsync(role);
        var pending = protocol.WorkerExitAsync(role);
        clock.Advance(TimeSpan.FromSeconds(30));
        var primary = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        var owners = new SplitScenarioResources(timeProvider: clock);
        owners.Add(role);
        var cleanup = owners.CompleteAsync(primary);
        await role.CleanupEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => cleanup);
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.IsType<TimeoutException>(failure.InnerExceptions[1]);
        Assert.True(role.WasTerminated && role.ProcessExited && role.PipesClosed);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("stop")]
    [InlineData("caller-exit")]
    [InlineData("worker-exit")]
    public async Task ProtocolFaultsCannotBeReportedAsSuccessfulCompletion(string boundary)
    {
        await using var role = new RoleProcess("lifetime-probe",
            boundary == "result" ? "wrong-result" : boundary == "stop" ? "reject-stop" : "nonzero");
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var protocol = new SplitScenarioProtocol(TimeProvider.System, TestContext.Current.CancellationToken);
        if (boundary is "result" or "stop")
        {
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.EqualException>(() => boundary == "result"
                ? protocol.OperationAsync(role, "RESULT probe") : protocol.StopWorkerAsync(role));
            Assert.Contains(boundary == "result" ? "RESULT" : "STOP", failure.Message);
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => boundary == "caller-exit"
                ? protocol.CallerExitAsync(role) : protocol.WorkerExitAsync(role));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFaultIsObservedAndOwnedProcessAndPipesAreClosed(bool rejectedUpdate)
    {
        var directory = ProcessStageDiagnostics.GetFixtureDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "invalid-seed.json");
        var repositoryPath = Path.Combine(directory, "entities");
        if (rejectedUpdate)
        {
            var data = new Data.Offline.FilesystemDataAccessLayer(repositoryPath);
            await data.UpdateAsync(new Data.UpdateRequest
            {
                UpdateMetadata = new Data.UpdateMetadata { Comment = new Data.Markdown { Text = "Prepare conflict." } },
                Changes = [new Data.EntityChange
                {
                    EntityId = User,
                    EntityChangeMode = Data.EntityChangeMode.Replace,
                    Data = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
                    {
                        ["entity-id"] = User.ToString(),
                        ["value"] = "original",
                    }),
                }],
            }, TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(path, rejectedUpdate
            ? $$"""[{"entity-id":"{{User}}","value":"changed"}]""" : "[{}]", TestContext.Current.CancellationToken);
        var role = new RoleProcess("prepare", repositoryPath, path);
        var owners = new SplitScenarioResources();
        owners.Add(role);
        var protocol = new SplitScenarioProtocol(TimeProvider.System, TestContext.Current.CancellationToken);
        var primary = await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.PrepareAsync(role));
        var failure = await Assert.ThrowsAsync<AggregateException>(() => owners.CompleteAsync(primary));
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(failure.InnerExceptions[1]);
        Assert.Contains("FAIL preparation " + (rejectedUpdate ? "InvalidOperationException" : "KeyNotFoundException"),
            role.DiagnosticOutput);
        Assert.True(role.ProcessExited && role.PipesClosed);
    }

    [Fact]
    public async Task ControlledPreparationCompletionLeavesTheFullRealProtocolOperationBudget()
    {
        var clock = new FakeTimeProvider();
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var preparation = new RoleProcess(clock, new RoleProcessHooks
        {
            AfterLine = async (line, token) =>
            {
                if (line.StartsWith("PREPARED ", StringComparison.Ordinal))
                {
                    preparationEntered.SetResult();
                    await preparationRelease.Task.WaitAsync(token);
                }
            },
        }, "lifetime-probe", "prepared");
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("READY probe", await preparation.NextLineAsync(cancellation));
        var protocol = new SplitScenarioProtocol(clock, cancellation, TimeSpan.FromSeconds(90));
        var prepared = protocol.PrepareAsync(preparation);
        await preparationEntered.Task.WaitAsync(cancellation);
        clock.Advance(TimeSpan.FromSeconds(89));
        preparationRelease.SetResult();
        Assert.Equal(Caller, (await prepared).UserComputerProfileEntityId);

        var resultEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var caller = new RoleProcess(clock, new RoleProcessHooks
        {
            AfterLine = async (line, token) =>
            {
                if (line == "RESULT probe")
                {
                    resultEntered.SetResult();
                    await resultRelease.Task.WaitAsync(token);
                }
            },
        }, "lifetime-probe", "protocol");
        Assert.Equal("READY probe", await caller.NextLineAsync(cancellation));
        var operation = protocol.OperationAsync(caller, "RESULT probe");
        await resultEntered.Task.WaitAsync(cancellation);
        clock.Advance(TimeSpan.FromSeconds(89));
        resultRelease.SetResult();
        await operation;
        await protocol.StopWorkerAsync(caller);
        await protocol.WorkerExitAsync(caller);
    }

    private sealed class CallbackOwner(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
