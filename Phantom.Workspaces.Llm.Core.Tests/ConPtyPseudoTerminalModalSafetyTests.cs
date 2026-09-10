using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ConPtyPseudoTerminalModalSafetyTests
{
    [Fact]
    public async Task ConPtyPseudoTerminal_ReadyChild_ReportsReadinessHandshake()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = WindowsProbeScenario.ConPty,
            PathCategory = WindowsProcessPathCategory.SystemBinary,
            LaunchMechanism = WindowsLaunchMechanism.CreateProcessWithConPty,
            Timeout = TimeSpan.FromSeconds(10),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.Equal(WindowsLaunchMechanism.CreateProcessWithConPty, result.LaunchMechanism);
        Assert.True(result.JobAssigned);
        Assert.True(result.ResumeSucceeded);
    }

    [Fact]
    public async Task ConPtyPseudoTerminal_DisposeAfterLaunchFailure_ClosesProcessJobAndPseudoConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = WindowsProbeScenario.ConPtyLaunchFailure,
            PathCategory = WindowsProcessPathCategory.FixedMissingBinary,
            LaunchMechanism = WindowsLaunchMechanism.CreateProcessWithConPty,
            Timeout = TimeSpan.FromSeconds(10),
        });

        Assert.False(result.CreateProcessSucceeded);
        Assert.NotNull(result.CreateProcessWin32Error);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.PseudoConsoleHandleClosed);
        Assert.True(result.InputHandleClosed);
        Assert.True(result.OutputHandleClosed);
        Assert.True(result.AttributeListReleased);
    }

    [Theory]
    [InlineData(WindowsProbeScenario.ConPtyConfigureFailure, "ConfigureJob")]
    [InlineData(WindowsProbeScenario.ConPtyAssignFailure, "AssignJob")]
    [InlineData(WindowsProbeScenario.ConPtyResumeFailure, "ResumeThread")]
    public async Task ConPtyPseudoTerminal_LaunchStageFailure_ClosesEveryCreatedNativeHandle(
        WindowsProbeScenario scenario,
        string failureStage)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.FixedProbeBinary,
            LaunchMechanism = WindowsLaunchMechanism.CreateProcessWithConPty,
            Timeout = TimeSpan.FromSeconds(10),
        });

        Assert.True(result.CreateProcessSucceeded);
        Assert.Equal(failureStage, result.FailureStage);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.ProcessHandleClosed);
        Assert.True(result.ThreadHandleClosed);
        Assert.True(result.JobHandleClosed);
        Assert.True(result.PseudoConsoleHandleClosed);
        Assert.True(result.InputHandleClosed);
        Assert.True(result.OutputHandleClosed);
        Assert.True(result.AttributeListReleased);
    }
}
