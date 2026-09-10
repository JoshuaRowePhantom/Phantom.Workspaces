using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Install.Tests;

public sealed class MxcExecutableResolverModalSafetyTests
{
    [Theory]
    [InlineData(WindowsProbeScenario.CommandShim, WindowsLaunchMechanism.ResolverCommandInterpreter)]
    [InlineData(WindowsProbeScenario.MxcCommandShim, WindowsLaunchMechanism.MxcSpawn)]
    public async Task StdioCommandResolver_CmdShim_UsesModalSafeProbe(
        WindowsProbeScenario scenario,
        WindowsLaunchMechanism mechanism)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (scenario == WindowsProbeScenario.MxcCommandShim
            && Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.FixedCommandShim,
            LaunchMechanism = mechanism,
            Timeout = TimeSpan.FromSeconds(20),
            Containment = scenario == WindowsProbeScenario.MxcCommandShim
                ? new WindowsContainmentDescriptor
                {
                    PolicyType = "ProcessContainer",
                    PolicyIdentity = "fixed-probe-v1",
                }
                : null,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.Contains("PROBE_READY", result.StandardOutput, StringComparison.Ordinal);
    }
}
