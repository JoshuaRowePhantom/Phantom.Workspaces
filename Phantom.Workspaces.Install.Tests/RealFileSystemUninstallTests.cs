using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Regression tests over <see cref="RealFileSystem"/> and the uninstall path that exercise real
/// NTFS directory links, which the in-memory fake cannot model. These reproduce the bug where a
/// recursive delete of the app root followed the <c>current</c> junction into the active version
/// directory, corrupting the delete.
/// </summary>
public sealed class RealFileSystemUninstallTests
{
    [Fact]
    public async Task RunUninstall_WithRealCurrentJunction_RemovesEntireManagedTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sandbox = Path.Combine(Path.GetTempPath(), "phantom-uninstall-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(sandbox, "payload");
        var appRoot = Path.Combine(sandbox, "app");
        try
        {
            var fileSystem = new RealFileSystem();
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, InstallLayout.ApplicationExecutableName), "exe");

            var layout = new InstallLayout(fileSystem, appRoot);
            layout.Bootstrap(payload, "0.0.1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            // Sanity: the real current link is a reparse point resolving to versions\0.0.1.
            Assert.True(Directory.Exists(Path.Combine(appRoot, "current")));
            Assert.Equal("0.0.1", layout.ResolveCurrentVersion());

            var scheduledTasks = new FakeScheduledTasks();
            var startupTaskService = new StartupTaskService(scheduledTasks, layout.CurrentExecutablePath);
            var applyUpdateRunner = new ApplyUpdateRunner(
                layout,
                new FakeInstanceReleaseWaiter(released: true),
                new HealthGate(fileSystem, layout),
                new FakeProcessLauncher());
            var runner = new ManagementModeRunner(
                layout,
                fileSystem,
                new SystemClock(),
                new FakeProcessLauncher(),
                startupTaskService,
                applyUpdateRunner);

            var exitCode = await runner.RunAsync(CommandLineOptions.Parse("--uninstall", "--purge"), payload, "0.0.1");

            Assert.Equal(ExitCode.Success, exitCode);
            Assert.False(Directory.Exists(appRoot), "The managed app tree should be fully removed.");
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }
}
