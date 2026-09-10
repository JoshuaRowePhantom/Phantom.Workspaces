using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

[CollectionDefinition("MXC process isolation", DisableParallelization = true)]
public sealed class MxcProcessIsolationCollection
{
}

[Collection("MXC process isolation")]
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
        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = scenario,
            PathCategory = WindowsProcessPathCategory.SystemBinary,
            LaunchMechanism = mechanism,
            Timeout = TimeSpan.FromSeconds(20),
        });

        if (scenario is WindowsProbeScenario.MxcProcessExecutor or WindowsProbeScenario.MxcCommandShim
            && Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
        {
            Assert.False(result.CreationStatusAvailable);
            Assert.Null(result.CreateProcessSucceeded);
            Assert.Equal("MXC capability unavailable", result.StandardError);
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.Equal(mechanism, result.LaunchMechanism);
        if (scenario is WindowsProbeScenario.MxcProcessExecutor or WindowsProbeScenario.MxcCommandShim)
        {
            Assert.False(result.CreationStatusAvailable);
            Assert.Null(result.CreateProcessSucceeded);
            Assert.Equal("ProcessContainer", result.Containment?.PolicyType);
            Assert.Equal("compiled-mxc-policy", result.Containment?.PolicyIdentity);
        }
        else
        {
            Assert.True(result.CreationStatusAvailable);
            Assert.True(result.CreateProcessSucceeded);
        }
        Assert.DoesNotContain(Environment.UserName, result.ToSanitizedDiagnostic(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessExecutor_MxcContainedCmd_ReportsContainmentAndCompletes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var result = await WindowsChildProcessTestHarness.RunAsync(new()
        {
            Scenario = WindowsProbeScenario.MxcProcessExecutor,
            PathCategory = WindowsProcessPathCategory.SystemBinary,
            LaunchMechanism = WindowsLaunchMechanism.MxcSpawn,
            Timeout = TimeSpan.FromSeconds(20),
        });

        if (Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
        {
            Assert.False(result.CreationStatusAvailable);
            Assert.Null(result.CreateProcessSucceeded);
            Assert.Equal("MXC capability unavailable", result.StandardError);
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.CreationStatusAvailable);
        Assert.Null(result.CreateProcessSucceeded);
        Assert.Equal("ProcessContainer", result.Containment?.PolicyType);
        Assert.Equal("compiled-mxc-policy", result.Containment?.PolicyIdentity);
    }
}
