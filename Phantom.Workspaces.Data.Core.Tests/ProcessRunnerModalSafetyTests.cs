using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Data.Tests;

public sealed class ProcessRunnerModalSafetyTests
{
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
}
