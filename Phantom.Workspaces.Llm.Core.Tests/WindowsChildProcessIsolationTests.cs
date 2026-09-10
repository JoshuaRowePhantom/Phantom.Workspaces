using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WindowsChildProcessIsolationTests
{
    public static TheoryData<WindowsProbeScenario, WindowsLaunchMechanism> Matrix => new()
    {
        { WindowsProbeScenario.Direct, WindowsLaunchMechanism.DirectCreateProcess },
        { WindowsProbeScenario.ConPty, WindowsLaunchMechanism.CreateProcessWithConPty },
        { WindowsProbeScenario.OrdinaryProcessExecutor, WindowsLaunchMechanism.OrdinaryProcess },
        { WindowsProbeScenario.MxcProcessExecutor, WindowsLaunchMechanism.MxcSpawn },
        { WindowsProbeScenario.CommandShim, WindowsLaunchMechanism.ResolverCommandInterpreter },
        { WindowsProbeScenario.MxcCommandShim, WindowsLaunchMechanism.MxcSpawn },
        { WindowsProbeScenario.ProcessRunnerNoJob, WindowsLaunchMechanism.JobRunner },
        { WindowsProbeScenario.ProcessRunnerKillTree, WindowsLaunchMechanism.JobRunner },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task WindowsChildProcessIsolation_LaunchMatrix_IdentifiesFirstFailingDimension(
        WindowsProbeScenario scenario,
        WindowsLaunchMechanism mechanism)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (scenario is WindowsProbeScenario.MxcProcessExecutor or WindowsProbeScenario.MxcCommandShim
            && Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.SystemBinary,
            LaunchMechanism = mechanism,
            Timeout = TimeSpan.FromSeconds(20),
            Containment = scenario is WindowsProbeScenario.MxcProcessExecutor or WindowsProbeScenario.MxcCommandShim
                ? new WindowsContainmentDescriptor
                {
                    PolicyType = "ProcessContainer",
                    PolicyIdentity = "fixed-probe-v1",
                }
                : null,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.Equal(mechanism, result.LaunchMechanism);
        Assert.DoesNotContain(Environment.UserName, result.ToSanitizedDiagnostic(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessExecutor_MxcContainedCmd_ReportsContainmentAndCompletes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = WindowsProbeScenario.MxcProcessExecutor,
            PathCategory = WindowsProcessPathCategory.SystemBinary,
            LaunchMechanism = WindowsLaunchMechanism.MxcSpawn,
            Timeout = TimeSpan.FromSeconds(20),
            Containment = new WindowsContainmentDescriptor
            {
                PolicyType = "ProcessContainer",
                PolicyIdentity = "fixed-probe-v1",
            },
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ProcessContainer", result.Containment?.PolicyType);
        Assert.Equal("fixed-probe-v1", result.Containment?.PolicyIdentity);
    }
}
