using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class ApplyUpdateRunnerTests
{
    private const string AppRoot = @"C:\sandbox\app";

    private static (InMemoryFileSystem FileSystem, InstallLayout Layout) NewLayout(params string[] versions)
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        foreach (var version in versions)
        {
            fileSystem.CreateDirectory(layout.GetVersionDirectory(version));
        }

        return (fileSystem, layout);
    }

    [Fact]
    public async Task RunAsync_RepointsCurrentRetainsPreviousAndExitsSuccess()
    {
        var (fileSystem, layout) = NewLayout("0.1.0", "0.0.9", "0.2.0");
        layout.RepointCurrent("0.1.0");
        var healthGate = new HealthGate(fileSystem, layout);
        var runner = new ApplyUpdateRunner(layout, new FakeInstanceReleaseWaiter(), healthGate, new FakeProcessLauncher());

        var exitCode = await runner.RunAsync(layout.GetVersionDirectory("0.2.0"), relaunch: false);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal("0.2.0", layout.ResolveCurrentVersion());
        var versions = layout.GetInstalledVersions();
        Assert.Contains("0.2.0", versions);
        Assert.Contains("0.1.0", versions);
        Assert.DoesNotContain("0.0.9", versions);
        Assert.Equal("0.2.0", healthGate.Read()?.PendingVersion);
        Assert.Equal("0.1.0", healthGate.Read()?.RollbackVersion);
    }

    [Fact]
    public async Task RunAsync_RelaunchStartsCurrentExecutable()
    {
        var (fileSystem, layout) = NewLayout("0.1.0", "0.2.0");
        layout.RepointCurrent("0.1.0");
        var launcher = new FakeProcessLauncher();
        var runner = new ApplyUpdateRunner(
            layout, new FakeInstanceReleaseWaiter(), new HealthGate(fileSystem, layout), launcher);

        var exitCode = await runner.RunAsync(layout.GetVersionDirectory("0.2.0"), relaunch: true);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Single(launcher.Requests);
        Assert.Equal(layout.CurrentExecutablePath, launcher.Requests[0].FileName);
        Assert.Equal(new[] { StartupTaskService.StartupArgument }, launcher.Requests[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureAndLeavesCurrentUntouchedWhenLockNeverReleases()
    {
        var (fileSystem, layout) = NewLayout("0.1.0", "0.2.0");
        layout.RepointCurrent("0.1.0");
        var runner = new ApplyUpdateRunner(
            layout,
            new FakeInstanceReleaseWaiter(released: false),
            new HealthGate(fileSystem, layout),
            new FakeProcessLauncher());

        var exitCode = await runner.RunAsync(layout.GetVersionDirectory("0.2.0"), relaunch: true);

        Assert.Equal(ExitCode.UpdateApplyFailure, exitCode);
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureWhenStagedVersionMissing()
    {
        var (fileSystem, layout) = NewLayout("0.1.0");
        layout.RepointCurrent("0.1.0");
        var runner = new ApplyUpdateRunner(
            layout, new FakeInstanceReleaseWaiter(), new HealthGate(fileSystem, layout), new FakeProcessLauncher());

        var exitCode = await runner.RunAsync(layout.GetVersionDirectory("9.9.9"), relaunch: false);

        Assert.Equal(ExitCode.UpdateApplyFailure, exitCode);
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
    }
}
