using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class ManagementModeRunnerTests
{
    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string AppRoot = @"X:\sandbox\Phantom.Workspaces\app";
    private const string PayloadDirectory = @"X:\program files\Phantom.Workspaces";

    private sealed class Harness
    {
        public InMemoryFileSystem FileSystem { get; } = new();

        public ManualClock Clock { get; } = new(FixedInstant);

        public FakeProcessLauncher ProcessLauncher { get; } = new();

        public FakeScheduledTasks ScheduledTasks { get; } = new();

        public FakeInstanceReleaseWaiter ReleaseWaiter { get; } = new(released: true);

        public InstallLayout Layout { get; }

        public ManagementModeRunner Runner { get; }

        public Harness()
        {
            this.Layout = new InstallLayout(this.FileSystem, AppRoot);
            var startupTaskService = new StartupTaskService(this.ScheduledTasks, this.Layout.CurrentExecutablePath);
            var healthGate = new HealthGate(this.FileSystem, this.Layout);
            var applyUpdateRunner = new ApplyUpdateRunner(
                this.Layout,
                this.ReleaseWaiter,
                healthGate,
                this.ProcessLauncher);
            this.Runner = new ManagementModeRunner(
                this.Layout,
                this.FileSystem,
                this.Clock,
                this.ProcessLauncher,
                startupTaskService,
                applyUpdateRunner);
        }

        public string SeedPayload()
        {
            this.FileSystem.CreateDirectory(PayloadDirectory);
            this.FileSystem.WriteAllText(
                Path.Combine(PayloadDirectory, InstallLayout.ApplicationExecutableName),
                "executable-bytes");
            return PayloadDirectory;
        }
    }

    [Theory]
    [InlineData(LaunchMode.Install, true)]
    [InlineData(LaunchMode.ApplyUpdate, true)]
    [InlineData(LaunchMode.Uninstall, true)]
    [InlineData(LaunchMode.Gui, false)]
    [InlineData(LaunchMode.Startup, false)]
    [InlineData(LaunchMode.Minimized, false)]
    [InlineData(LaunchMode.Help, false)]
    public void IsManagementMode_ClassifiesModes(LaunchMode mode, bool expected)
    {
        Assert.Equal(expected, ManagementModeRunner.IsManagementMode(mode));
    }

    [Fact]
    public async Task RunAsync_InvalidOptions_ReturnsParseExitCode()
    {
        var harness = new Harness();
        var options = CommandLineOptions.Parse("--bogus");

        var exitCode = await harness.Runner.RunAsync(options, PayloadDirectory, "0.1.0");

        Assert.Equal(ExitCode.BadArguments, exitCode);
    }

    [Fact]
    public async Task RunAsync_InstallSilent_BootstrapsWithoutLaunching()
    {
        var harness = new Harness();
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install", "--silent");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal("0.1.0", harness.Layout.ResolveCurrentVersion());
        Assert.Empty(harness.ProcessLauncher.Requests);
    }

    [Fact]
    public async Task RunAsync_InstallInteractive_BootstrapsThenLaunchesCurrentExecutable()
    {
        var harness = new Harness();
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal("0.1.0", harness.Layout.ResolveCurrentVersion());
        var request = Assert.Single(harness.ProcessLauncher.Requests);
        Assert.Equal(harness.Layout.CurrentExecutablePath, request.FileName);
    }

    [Fact]
    public async Task RunAsync_ApplyUpdate_RepointsToStagedVersion()
    {
        var harness = new Harness();
        var payload = harness.SeedPayload();
        harness.Layout.Bootstrap(payload, "0.1.0", FixedInstant);
        var stagedDirectory = harness.Layout.GetVersionDirectory("0.2.0");
        harness.FileSystem.CreateDirectory(stagedDirectory);
        var options = CommandLineOptions.Parse("--apply-update", stagedDirectory);

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.2.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal("0.2.0", harness.Layout.ResolveCurrentVersion());
        Assert.Equal(1, harness.ReleaseWaiter.WaitCount);
    }

    [Fact]
    public async Task RunAsync_Uninstall_RemovesStartupTaskAndManagedTree()
    {
        var harness = new Harness();
        var payload = harness.SeedPayload();
        harness.Layout.Bootstrap(payload, "0.1.0", FixedInstant);
        harness.ScheduledTasks.Register(new ScheduledTaskDefinition
        {
            TaskName = StartupTaskService.StartupTaskName,
            ExecutablePath = harness.Layout.CurrentExecutablePath,
            Arguments = new[] { StartupTaskService.StartupArgument },
        });
        var options = CommandLineOptions.Parse("--uninstall");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.False(harness.ScheduledTasks.Exists(StartupTaskService.StartupTaskName));
        Assert.False(harness.FileSystem.DirectoryExists(harness.Layout.AppRoot));
    }

    [Fact]
    public async Task RunAsync_Uninstall_WhenAppRootMissing_StillSucceeds()
    {
        var harness = new Harness();
        var options = CommandLineOptions.Parse("--uninstall");

        var exitCode = await harness.Runner.RunAsync(options, PayloadDirectory, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
    }

    [Fact]
    public async Task RunAsync_NonManagementMode_Throws()
    {
        var harness = new Harness();
        var options = CommandLineOptions.Parse();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Runner.RunAsync(options, PayloadDirectory, "0.1.0"));
    }
}
