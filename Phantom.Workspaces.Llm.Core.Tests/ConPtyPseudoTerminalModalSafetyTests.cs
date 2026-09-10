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

        Assert.True(
            result.ExitCode == 0,
            $"{result.ToSanitizedDiagnostic()}; error={result.StandardError}; terminal={result.TerminalOutput}");
        Assert.True(
            result.ReadinessHandshakeObserved,
            $"{result.ToSanitizedDiagnostic()}; terminal={result.TerminalOutput}");
        Assert.NotEmpty(result.TerminalOutput);
        Assert.Equal(WindowsLaunchMechanism.CreateProcessWithConPty, result.LaunchMechanism);
        Assert.True(result.JobConfigured);
        Assert.True(result.JobAssigned);
        Assert.True(result.ResumeSucceeded);
        Assert.True(result.InnerJobAssigned);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.BrokerJobAssigned);
        Assert.True(result.BrokerResumeSucceeded);
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
    [InlineData(WindowsProbeScenario.ConPtyConfigureFailure, "ConfigureJob", false, false, false)]
    [InlineData(WindowsProbeScenario.ConPtyAssignFailure, "AssignJob", true, false, false)]
    [InlineData(WindowsProbeScenario.ConPtyResumeFailure, "ResumeThread", true, true, false)]
    public async Task ConPtyPseudoTerminal_LaunchStageFailure_ClosesEveryCreatedNativeHandle(
        WindowsProbeScenario scenario,
        string failureStage,
        bool jobConfigured,
        bool jobAssigned,
        bool resumed)
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
        Assert.Equal(jobConfigured, result.JobConfigured);
        Assert.Equal(jobAssigned, result.JobAssigned);
        Assert.Equal(resumed, result.ResumeSucceeded);
        Assert.Equal(jobAssigned, result.InnerJobAssigned);
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
