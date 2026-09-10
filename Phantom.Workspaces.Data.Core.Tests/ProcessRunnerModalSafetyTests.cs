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
        Assert.Equal(expectsInnerJob, result.InnerJobAssigned);
    }
}
