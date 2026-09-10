using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

[CollectionDefinition("MXC process isolation", DisableParallelization = true)]
public sealed class MxcProcessIsolationCollection
{
}

[Collection("MXC process isolation")]
public sealed class WindowsChildProcessIsolationTests
{
    private static readonly (WindowsProbeScenario Scenario, WindowsLaunchMechanism Mechanism,
        WindowsProcessPathCategory PathCategory)[] Matrix =
    [
        (WindowsProbeScenario.Direct, WindowsLaunchMechanism.DirectCreateProcess,
            WindowsProcessPathCategory.SystemBinary),
        (WindowsProbeScenario.ConPty, WindowsLaunchMechanism.CreateProcessWithConPty,
            WindowsProcessPathCategory.SystemBinary),
        (WindowsProbeScenario.OrdinaryProcessExecutor, WindowsLaunchMechanism.OrdinaryProcess,
            WindowsProcessPathCategory.SystemBinary),
        (WindowsProbeScenario.MxcProcessExecutor, WindowsLaunchMechanism.MxcSpawn,
            WindowsProcessPathCategory.SystemBinary),
        (WindowsProbeScenario.CommandShim, WindowsLaunchMechanism.ResolverCommandInterpreter,
            WindowsProcessPathCategory.FixedCommandShim),
        (WindowsProbeScenario.MxcCommandShim, WindowsLaunchMechanism.MxcSpawn,
            WindowsProcessPathCategory.FixedCommandShim),
        (WindowsProbeScenario.ProcessRunnerNoJob, WindowsLaunchMechanism.JobRunner,
            WindowsProcessPathCategory.SystemBinary),
        (WindowsProbeScenario.ProcessRunnerKillTree, WindowsLaunchMechanism.JobRunner,
            WindowsProcessPathCategory.SystemBinary),
    ];

    [Fact]
    public async Task WindowsChildProcessIsolation_LaunchMatrix_IdentifiesFirstFailingDimension()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var results = new Dictionary<WindowsProbeScenario, WindowsChildProcessProbeResult>();
        foreach (var (scenario, mechanism, pathCategory) in Matrix)
        {
            var result = await WindowsChildProcessTestHarness.RunAsync(new()
            {
                Scenario = scenario,
                PathCategory = pathCategory,
                LaunchMechanism = mechanism,
                Timeout = TimeSpan.FromSeconds(20),
            });
            results.Add(scenario, result);

            Assert.Equal(pathCategory, result.PathCategory);
            Assert.Equal(mechanism, result.LaunchMechanism);
            Assert.NotNull(result.ExecutableImageIdentity);

            var mxcUnavailable = scenario is WindowsProbeScenario.MxcProcessExecutor
                    or WindowsProbeScenario.MxcCommandShim
                && Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1";
            if (mxcUnavailable)
            {
                Assert.False(result.CreationStatusAvailable);
                Assert.Null(result.CreateProcessSucceeded);
                Assert.Null(result.SdkSpawnSucceeded);
                Assert.Null(result.JobConfigured);
                Assert.Null(result.JobAssigned);
                Assert.Null(result.ResumeSucceeded);
                Assert.Null(result.CleanupCompleted);
                Assert.Null(result.Containment);
                Assert.Equal("MXC capability unavailable", result.StandardError);
                continue;
            }

            Assert.Equal(0, result.ExitCode);
            Assert.True(
                result.ReadinessHandshakeObserved,
                $"{scenario}: {result.ToSanitizedDiagnostic()}; terminal={result.TerminalOutput}");
            Assert.DoesNotContain(
                Environment.UserName,
                result.ToSanitizedDiagnostic(),
                StringComparison.OrdinalIgnoreCase);

            switch (scenario)
            {
                case WindowsProbeScenario.Direct:
                case WindowsProbeScenario.OrdinaryProcessExecutor:
                case WindowsProbeScenario.CommandShim:
                    Assert.True(result.CreationStatusAvailable);
                    Assert.True(result.CreateProcessSucceeded);
                    Assert.Null(result.SdkSpawnSucceeded);
                    Assert.Null(result.JobConfigured);
                    Assert.Null(result.JobAssigned);
                    Assert.Null(result.ResumeSucceeded);
                    break;
                case WindowsProbeScenario.ConPty:
                    Assert.True(result.CreateProcessSucceeded);
                    Assert.NotEmpty(result.TerminalOutput);
                    Assert.True(result.JobConfigured);
                    Assert.True(result.JobAssigned);
                    Assert.True(result.ResumeSucceeded);
                    Assert.True(result.CleanupCompleted);
                    break;
                case WindowsProbeScenario.MxcProcessExecutor:
                case WindowsProbeScenario.MxcCommandShim:
                    Assert.False(result.CreationStatusAvailable);
                    Assert.Null(result.CreateProcessSucceeded);
                    Assert.True(result.SdkSpawnSucceeded);
                    Assert.Null(result.JobConfigured);
                    Assert.Null(result.JobAssigned);
                    Assert.Null(result.ResumeSucceeded);
                    Assert.True(result.CleanupCompleted);
                    Assert.Equal(
                        "ProcessContainerContainment",
                        result.Containment?.PolicyType);
                    Assert.StartsWith(
                        "ProcessContainerContainment:",
                        result.Containment?.PolicyIdentity,
                        StringComparison.Ordinal);
                    break;
                case WindowsProbeScenario.ProcessRunnerNoJob:
                    Assert.Null(result.JobConfigured);
                    Assert.Null(result.JobAssigned);
                    Assert.Null(result.ResumeSucceeded);
                    Assert.Null(result.InnerJobAssigned);
                    break;
                case WindowsProbeScenario.ProcessRunnerKillTree:
                    Assert.True(result.JobConfigured);
                    Assert.True(result.JobAssigned);
                    Assert.True(result.ResumeSucceeded);
                    Assert.True(result.InnerJobAssigned);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected matrix scenario {scenario}.");
            }
        }

        AssertSameImage(
            results,
            WindowsProbeScenario.Direct,
            WindowsProbeScenario.ConPty,
            WindowsProbeScenario.OrdinaryProcessExecutor,
            WindowsProbeScenario.MxcProcessExecutor,
            WindowsProbeScenario.CommandShim,
            WindowsProbeScenario.MxcCommandShim,
            WindowsProbeScenario.ProcessRunnerNoJob,
            WindowsProbeScenario.ProcessRunnerKillTree);
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

        Assert.Equal(WindowsProcessPathCategory.SystemBinary, result.PathCategory);
        Assert.NotNull(result.ExecutableImageIdentity);
        if (Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
        {
            Assert.False(result.CreationStatusAvailable);
            Assert.Null(result.CreateProcessSucceeded);
            Assert.Null(result.SdkSpawnSucceeded);
            Assert.Null(result.Containment);
            Assert.Equal("MXC capability unavailable", result.StandardError);
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.CreationStatusAvailable);
        Assert.Null(result.CreateProcessSucceeded);
        Assert.True(result.SdkSpawnSucceeded);
        Assert.Null(result.JobConfigured);
        Assert.Null(result.JobAssigned);
        Assert.Null(result.ResumeSucceeded);
        Assert.True(result.CleanupCompleted);
        Assert.Equal("ProcessContainerContainment", result.Containment?.PolicyType);
        Assert.StartsWith(
            "ProcessContainerContainment:",
            result.Containment?.PolicyIdentity,
            StringComparison.Ordinal);
    }

    private static void AssertSameImage(
        IReadOnlyDictionary<WindowsProbeScenario, WindowsChildProcessProbeResult> results,
        params WindowsProbeScenario[] scenarios)
    {
        var expected = results[scenarios[0]].ExecutableImageIdentity;
        Assert.NotNull(expected);
        Assert.All(
            scenarios,
            scenario => Assert.Equal(expected, results[scenario].ExecutableImageIdentity));
    }
}
