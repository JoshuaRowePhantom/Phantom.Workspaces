using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Install.Tests;

[Collection(MxcIntegrationCollection.Name)]
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
        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.FixedCommandShim,
            LaunchMechanism = mechanism,
            Timeout = TimeSpan.FromSeconds(20),
        });

        if (scenario == WindowsProbeScenario.MxcCommandShim
            && Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
        {
            Assert.False(result.CreationStatusAvailable);
            Assert.Null(result.CreateProcessSucceeded);
            Assert.Equal("MXC capability unavailable", result.StandardError);
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.Contains("PROBE_READY", result.StandardOutput, StringComparison.Ordinal);
        if (scenario == WindowsProbeScenario.MxcCommandShim)
        {
            Assert.False(result.CreationStatusAvailable);
            Assert.Null(result.CreateProcessSucceeded);
            Assert.Equal("ProcessContainer", result.Containment?.PolicyType);
            Assert.Equal("compiled-mxc-policy", result.Containment?.PolicyIdentity);
        }
    }
}
