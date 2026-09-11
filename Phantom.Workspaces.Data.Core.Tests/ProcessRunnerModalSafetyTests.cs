using System.ComponentModel;

using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Data.Tests;

public sealed class ProcessRunnerModalSafetyTests
{
    [Theory]
    [InlineData(nameof(ProcessRunnerWindowsStage.ConfigureJob))]
    [InlineData(nameof(ProcessRunnerWindowsStage.AssignJob))]
    [InlineData(nameof(ProcessRunnerWindowsStage.ResumeThread))]
    public async Task ProcessRunner_LaunchStageFailure_CleanupDoesNotSynchronouslyBlockCaller(
        string failureStageName)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var failureStage = Enum.Parse<ProcessRunnerWindowsStage>(failureStageName);
        var cleanupCanContinue = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = false;
        var observer = new RecordingObserver();
        var originalContext = SynchronizationContext.Current;
        var uiContext = new RejectingSynchronizationContext();
        Task<ProcessResult> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            operation = ProcessRunner.RunProcessAsync(new RunProcessParameters(
                Command: "cmd.exe",
                Arguments: ["/d", "/c", "exit", "0"],
                KillOnClose: KillOnCloseAction.KillTree)
            {
                WindowsTestOptions = new()
                {
                    InjectFailureAt = failureStage,
                    Observer = observer,
                    BeforeFailureCleanupWaitAsync = () =>
                    {
                        cleanupStarted = true;
                        return cleanupCanContinue.Task;
                    },
                },
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        Assert.True(cleanupStarted);
        Assert.False(operation.IsCompleted);
        string[] expectedStages = failureStage switch
        {
            ProcessRunnerWindowsStage.ConfigureJob =>
                ["CreateProcess:True", "ConfigureJob:False"],
            ProcessRunnerWindowsStage.AssignJob =>
                ["CreateProcess:True", "ConfigureJob:True", "AssignJob:False"],
            ProcessRunnerWindowsStage.ResumeThread =>
                ["CreateProcess:True", "ConfigureJob:True", "AssignJob:True", "ResumeThread:False"],
            _ => throw new ArgumentOutOfRangeException(nameof(failureStageName)),
        };
        Assert.Equal(
            expectedStages,
            observer.Events.Select(processEvent => $"{processEvent.Stage}:{processEvent.Succeeded}"));
        Assert.DoesNotContain(
            observer.Events,
            processEvent => processEvent.Stage == ProcessRunnerWindowsStage.ReleaseResource);

        cleanupCanContinue.SetResult();

        var exception = await Assert.ThrowsAsync<Win32Exception>(() => operation);
        Assert.Equal(31, exception.NativeErrorCode);
        Assert.Equal($"Injected {failureStage} failure.", exception.Message);
        Assert.Equal(0, uiContext.CallbackCount);
        AssertReleased(observer, ProcessRunnerWindowsResource.Process, 1);
        AssertReleased(observer, ProcessRunnerWindowsResource.Thread, 1);
        AssertReleased(observer, ProcessRunnerWindowsResource.Job, 1);
        AssertReleased(observer, ProcessRunnerWindowsResource.StandardOutputPipe, 2);
        AssertReleased(observer, ProcessRunnerWindowsResource.StandardErrorPipe, 2);
    }

    [Theory]
    [InlineData(WindowsProbeScenario.ProcessRunnerNoJob, false)]
    [InlineData(WindowsProbeScenario.ProcessRunnerKillTree, true)]
    public async Task ProcessRunner_KillTreeJob_UsesModalSafeProbe(
        WindowsProbeScenario scenario,
        bool expectsInnerJob)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.SystemBinary,
            LaunchMechanism = WindowsLaunchMechanism.JobRunner,
            Timeout = TimeSpan.FromSeconds(10),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.Equal(expectsInnerJob ? true : null, result.InnerJobAssigned);
        Assert.Equal(
            expectsInnerJob ? true : null,
            result.JobConfigured);
        Assert.Equal(
            expectsInnerJob ? true : null,
            result.JobAssigned);
        Assert.Equal(
            expectsInnerJob ? true : null,
            result.ResumeSucceeded);
    }

    [Fact]
    public async Task ProcessRunner_Timeout_ClosesKillOnCloseJobAndDescendantTree()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(WindowsProbeScenario.ProcessRunnerTimeoutTree);

        Assert.True(result.TimedOut, $"{result.StandardError}; {result.TerminalOutput}");
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.True(result.ChildExitObserved);
        Assert.True(result.DescendantExitObserved);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.ProcessHandleClosed);
        Assert.True(result.ThreadHandleClosed);
        Assert.True(result.JobHandleClosed);
        Assert.True(result.OutputHandleClosed);
    }

    [Fact]
    public async Task ProcessRunner_Cancellation_ClosesKillOnCloseJobAndDescendantTree()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(WindowsProbeScenario.ProcessRunnerCancellationTree);

        Assert.False(result.TimedOut);
        Assert.True(
            result.FailureStage == "Cancellation",
            $"{result.StandardError}; {result.TerminalOutput}");
        Assert.True(result.ChildExitObserved);
        Assert.True(result.DescendantExitObserved);
        Assert.True(result.CleanupCompleted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProcessRunner_ExitedParent_KillsPipeHoldingDescendantAndCompletesDrains(
        int _)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(WindowsProbeScenario.ProcessRunnerExitedParentTree);

        Assert.Equal(23, result.ExitCode);
        Assert.Contains("parent-stdout", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("parent-stderr", result.StandardError, StringComparison.Ordinal);
        Assert.True(result.DescendantExitObserved);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.ProcessHandleClosed);
        Assert.True(result.ThreadHandleClosed);
        Assert.True(result.JobHandleClosed);
        Assert.True(result.OutputHandleClosed);
    }

    [Fact]
    public async Task ProcessRunner_OutputDrainExpiry_CancelsReadsAndPreservesPartialDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var time = new ManualDrainTimeProvider();
        var observer = new RecordingObserver();
        var operation = ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments:
            [
                "/d",
                "/s",
                "/c",
                "echo retained-stdout && echo retained-stderr 1>&2",
            ],
            KillOnClose: KillOnCloseAction.KillTree)
        {
            WindowsTestOptions = new()
            {
                Observer = observer,
                TimeProvider = time,
                OutputDrainDecorator = HoldDrainUntilCancelledAsync,
            },
        });

        await time.TimerArmed;
        time.Expire();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => operation);
        Assert.Contains("retained-stdout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retained-stderr", exception.Message, StringComparison.Ordinal);
        AssertReleased(observer, ProcessRunnerWindowsResource.Process, 1);
        AssertReleased(observer, ProcessRunnerWindowsResource.Thread, 1);
        AssertReleased(observer, ProcessRunnerWindowsResource.Job, 1);
        AssertReleased(observer, ProcessRunnerWindowsResource.StandardOutputPipe, 2);
        AssertReleased(observer, ProcessRunnerWindowsResource.StandardErrorPipe, 2);
    }

    [Theory]
    [InlineData(WindowsProbeScenario.ProcessRunnerConfigureFailure, "ConfigureJob")]
    [InlineData(WindowsProbeScenario.ProcessRunnerAssignFailure, "AssignJob")]
    [InlineData(WindowsProbeScenario.ProcessRunnerResumeFailure, "ResumeThread")]
    public async Task ProcessRunner_LaunchStageFailure_FailsClosedAndReleasesHandles(
        WindowsProbeScenario scenario,
        string failureStage)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await RunAsync(scenario);

        Assert.True(result.CreateProcessSucceeded);
        Assert.Equal(failureStage, result.FailureStage);
        Assert.Null(result.ExitCode);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.ProcessHandleClosed);
        Assert.True(result.ThreadHandleClosed);
        Assert.True(result.JobHandleClosed);
        Assert.True(result.OutputHandleClosed);
    }

    private static Task<WindowsChildProcessProbeResult> RunAsync(WindowsProbeScenario scenario) =>
        WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.FixedProbeBinary,
            LaunchMechanism = WindowsLaunchMechanism.JobRunner,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static void AssertReleased(
        RecordingObserver observer,
        ProcessRunnerWindowsResource resource,
        int expectedCount)
    {
        var releases = observer.Events.Where(
            processEvent => processEvent.Stage == ProcessRunnerWindowsStage.ReleaseResource
                && processEvent.Resource == resource).ToArray();
        Assert.Equal(expectedCount, releases.Length);
        Assert.All(releases, processEvent => Assert.True(processEvent.Succeeded));
    }

    private static async Task HoldDrainUntilCancelledAsync(
        Task actualDrain,
        CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => cancelled.TrySetCanceled(cancellationToken));
        await Task.WhenAll(actualDrain, cancelled.Task);
    }

    private sealed class RecordingObserver : IProcessRunnerWindowsObserver
    {
        private readonly List<ProcessRunnerWindowsEvent> events = [];

        public IReadOnlyList<ProcessRunnerWindowsEvent> Events
        {
            get
            {
                lock (events)
                    return events.ToArray();
            }
        }

        public void Observe(ProcessRunnerWindowsEvent processEvent)
        {
            lock (events)
                events.Add(processEvent);
        }
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        private int callbackCount;

        public int CallbackCount => Volatile.Read(ref callbackCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref callbackCount);
            throw new InvalidOperationException("Failure cleanup captured the caller context.");
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref callbackCount);
            throw new InvalidOperationException("Failure cleanup synchronously dispatched to the caller.");
        }
    }

    private sealed class ManualDrainTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource timerArmed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualTimer? timer;

        public Task TimerArmed => timerArmed.Task;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            timer = new ManualTimer(callback, state);
            timerArmed.TrySetResult();
            return timer;
        }

        public void Expire() =>
            (timer ?? throw new InvalidOperationException("The drain timer was not armed.")).Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;

            public void Fire()
            {
                if (!disposed)
                    callback(state);
            }

            public void Dispose() => disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
