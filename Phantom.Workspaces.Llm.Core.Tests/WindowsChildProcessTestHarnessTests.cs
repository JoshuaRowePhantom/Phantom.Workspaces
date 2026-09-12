using System.Runtime.InteropServices;
using Phantom.Workspaces.Testing.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

[Collection("MXC process isolation")]
public sealed class WindowsChildProcessTestHarnessTests
{
    [Fact]
    public async Task WindowsChildProcessHarness_FailingChild_SuppressesApplicationErrorDialog()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(Request(WindowsProbeScenario.FailingChild));

        Assert.True(result.BrokerCreateProcessSucceeded);
        Assert.True(result.CreateProcessSucceeded);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.False(result.TimedOut);
        Assert.True(result.BrokerJobConfigured);
        Assert.True(result.BrokerJobAssigned);
        Assert.True(result.BrokerResumeSucceeded);
        Assert.Null(result.JobConfigured);
        Assert.Null(result.JobAssigned);
        Assert.Null(result.ResumeSucceeded);
        Assert.True(result.CleanupCompleted);
    }

    [Fact]
    public async Task WindowsChildProcessHarness_StatusDllInitFailed_ReportsUnsignedNtStatus()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(
            Request(WindowsProbeScenario.StatusDllInitFailed));

        Assert.True(result.CreateProcessSucceeded);
        Assert.Equal(unchecked((int)0xC0000142), result.ExitCode);
        Assert.Equal("0xC0000142", result.UnsignedNtStatus);
        Assert.True(result.ReadinessHandshakeObserved);
    }

    [Fact]
    public async Task WindowsChildProcessHarness_ParallelLaunches_DoesNotChangeTestHostErrorMode()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var before = GetErrorMode();
        var results = await Task.WhenAll(
            WindowsChildProcessTestHarness.RunAsync(Request(WindowsProbeScenario.NormalChild)),
            WindowsChildProcessTestHarness.RunAsync(Request(WindowsProbeScenario.NormalChild)));

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(before, GetErrorMode());
    }

    [Fact]
    public async Task WindowsChildProcessHarness_NormalChild_DrainsStreamsAndReturnsZero()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(Request(WindowsProbeScenario.NormalChild));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("probe-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("probe-error", result.StandardError, StringComparison.Ordinal);
        Assert.True(result.ReadinessHandshakeObserved);
    }

    [Fact]
    public async Task WindowsChildProcessHarness_Timeout_ClosesJobAndProcessTree()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = await WindowsChildProcessTestHarness.RunAsync(Request(WindowsProbeScenario.TimeoutTree));

        Assert.True(result.TimedOut);
        Assert.True(result.ReadinessHandshakeObserved);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.ChildExitObserved);
        Assert.True(result.DescendantExitObserved);
        Assert.True(result.JobHandleClosed);
        Assert.True(result.ProcessHandleClosed);
        Assert.True(result.ThreadHandleClosed);
        Assert.Equal("0xC000013A", result.UnsignedNtStatus);
    }

    private static WindowsChildProcessProbeRequest Request(WindowsProbeScenario scenario) => new()
    {
        Scenario = scenario,
        PathCategory = WindowsProcessPathCategory.FixedProbeBinary,
        LaunchMechanism = WindowsLaunchMechanism.DirectCreateProcess,
        Timeout = scenario == WindowsProbeScenario.TimeoutTree
            ? TimeSpan.FromMilliseconds(250)
            : TimeSpan.FromSeconds(10),
    };

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}
