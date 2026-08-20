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

        public IProcessLauncher ProcessLauncherImpl { get; }

        public FakeProcessLauncher ProcessLauncher =>
            (this.ProcessLauncherImpl as FakeProcessLauncher)
                ?? throw new InvalidOperationException("ProcessLauncher is not a FakeProcessLauncher in this harness.");

        public IScheduledTasks ScheduledTasksImpl { get; }

        public FakeScheduledTasks ScheduledTasks =>
            (this.ScheduledTasksImpl as FakeScheduledTasks)
                ?? throw new InvalidOperationException("ScheduledTasks is not a FakeScheduledTasks in this harness.");

        public IStartupRegistration StartupRegistrationImpl { get; }

        public FakeStartupRegistration StartupRegistration =>
            (this.StartupRegistrationImpl as FakeStartupRegistration)
                ?? throw new InvalidOperationException("StartupRegistration is not a FakeStartupRegistration in this harness.");

        public FakeInstanceReleaseWaiter ReleaseWaiter { get; } = new(released: true);

        public InstallLayout Layout { get; }

        public ManagementModeRunner Runner { get; }

        public Harness(
            IProcessLauncher? processLauncher = null,
            IScheduledTasks? scheduledTasks = null,
            UpdateService? updateService = null,
            IStartupRegistration? startupRegistration = null)
        {
            this.ProcessLauncherImpl = processLauncher ?? new FakeProcessLauncher();
            this.ScheduledTasksImpl = scheduledTasks ?? new FakeScheduledTasks();
            this.StartupRegistrationImpl = startupRegistration ?? new FakeStartupRegistration();
            this.Layout = new InstallLayout(this.FileSystem, AppRoot);
            var startupTaskService = new StartupTaskService(this.StartupRegistrationImpl, this.ScheduledTasksImpl, this.Layout.CurrentExecutablePath);
            var healthGate = new HealthGate(this.FileSystem, this.Layout);
            // When testing throwing-process-launcher, ApplyUpdateRunner still needs *some* launcher —
            // give it a benign fake so its own paths are unaffected.
            var applyUpdateLauncher = this.ProcessLauncherImpl is FakeProcessLauncher fpl ? fpl : new FakeProcessLauncher();
            var applyUpdateRunner = new ApplyUpdateRunner(
                this.Layout,
                this.ReleaseWaiter,
                healthGate,
                applyUpdateLauncher);
            this.Runner = new ManagementModeRunner(
                this.Layout,
                this.FileSystem,
                this.Clock,
                this.ProcessLauncherImpl,
                startupTaskService,
                applyUpdateRunner,
                updateService);
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
    [InlineData(LaunchMode.Update, true)]
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
    public async Task RunAsync_InstallSilent_EnablesStartupTask()
    {
        var harness = new Harness();
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install", "--silent");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.True(harness.StartupRegistration.IsEnabled(StartupTaskService.StartupRunValueName));
        var commandLine = harness.StartupRegistration.Entries[StartupTaskService.StartupRunValueName];
        Assert.Contains(harness.Layout.CurrentExecutablePath, commandLine, StringComparison.Ordinal);
        Assert.Contains(StartupTaskService.StartupArgument, commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_InstallSilent_StartsInstalledExecutableWithStartupArgument()
    {
        var harness = new Harness();
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install", "--silent");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal("0.1.0", harness.Layout.ResolveCurrentVersion());
        var request = Assert.Single(harness.ProcessLauncher.Requests);
        Assert.Equal(harness.Layout.CurrentExecutablePath, request.FileName);
        Assert.NotNull(request.Arguments);
        Assert.Contains(StartupTaskService.StartupArgument, request.Arguments!);
        // #1302: fire-and-forget GUI launch must not inherit parent console handles.
        Assert.True(request.Detached);
    }

    [Fact]
    public async Task RunAsync_InstallInteractive_LaunchesGuiDetached()
    {
        // Same detached guarantee for the interactive --install path.
        var harness = new Harness();
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        var request = Assert.Single(harness.ProcessLauncher.Requests);
        Assert.True(request.Detached);
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
        Assert.True(harness.StartupRegistration.IsEnabled(StartupTaskService.StartupRunValueName));
    }

    [Fact]
    public async Task RunAsync_Install_WhenStartupTaskEnableFails_StillSucceedsAndLaunches()
    {
        var harness = new Harness(
            startupRegistration: new FakeStartupRegistration
            {
                EnableError = new InvalidOperationException("simulated run-at-startup failure"),
            });
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install", "--silent");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Equal("0.1.0", harness.Layout.ResolveCurrentVersion());
        // Launch still attempted even when startup-task enable threw.
        var request = Assert.Single(harness.ProcessLauncher.Requests);
        Assert.Equal(harness.Layout.CurrentExecutablePath, request.FileName);
    }

    [Fact]
    public async Task RunAsync_Install_WhenProcessLaunchFails_StillSucceedsAndEnablesStartupTask()
    {
        var harness = new Harness(processLauncher: new ThrowingProcessLauncher());
        var payload = harness.SeedPayload();
        var options = CommandLineOptions.Parse("--install", "--silent");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.True(harness.StartupRegistration.IsEnabled(StartupTaskService.StartupRunValueName));
    }

    private sealed class ThrowingScheduledTasks : IScheduledTasks
    {
        public bool Exists(string taskName) => false;
        public void Register(ScheduledTaskDefinition definition)
            => throw new InvalidOperationException("simulated Task Scheduler failure");
        public void Unregister(string taskName) { }
    }

    private sealed class ThrowingProcessLauncher : IProcessLauncher
    {
        public IProcessHandle Start(ProcessStartRequest request)
            => throw new InvalidOperationException("simulated process launch failure");
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
        harness.StartupRegistration.Enable(StartupTaskService.StartupRunValueName, "seeded");
        harness.ScheduledTasks.Register(new ScheduledTaskDefinition
        {
            TaskName = StartupTaskService.StartupTaskName,
            ExecutablePath = harness.Layout.CurrentExecutablePath,
            Arguments = new[] { StartupTaskService.StartupArgument },
        });
        var options = CommandLineOptions.Parse("--uninstall");

        var exitCode = await harness.Runner.RunAsync(options, payload, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.False(harness.StartupRegistration.IsEnabled(StartupTaskService.StartupRunValueName));
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
    public async Task RunAsync_Update_WhenNewerReleaseAvailable_StagesAndSpawnsApplyUpdate()
    {
        var release = new ReleaseInfo
        {
            TagName = "v0.2.0",
            Assets =
            [
                new ReleaseAsset
                {
                    Name = "Phantom.Workspaces-win-x64.zip",
                    DownloadUrl = "https://example/win-x64.zip",
                },
            ],
        };
        var fs = new InMemoryFileSystem();
        var layout = new InstallLayout(fs, AppRoot);
        // Seed initial current so ResolveCurrentVersion works.
        fs.CreateDirectory(layout.GetVersionDirectory("0.1.0"));
        layout.RepointCurrent("0.1.0");
        var updateService = new UpdateService(
            new FakeReleaseSource(release),
            new FakeUpdateDownloader(fs, new byte[] { 1, 2 }),
            new FakeArchiveExtractor(fs),
            fs,
            layout,
            runningVersion: "0.1.0",
            assetMoniker: "win-x64");

        var launcher = new FakeProcessLauncher();
        var startupTaskService = new StartupTaskService(new FakeStartupRegistration(), new FakeScheduledTasks(), layout.CurrentExecutablePath);
        var applyUpdateRunner = new ApplyUpdateRunner(
            layout, new FakeInstanceReleaseWaiter(released: true), new HealthGate(fs, layout), launcher);
        var runner = new ManagementModeRunner(
            layout, fs, new ManualClock(FixedInstant), launcher, startupTaskService, applyUpdateRunner, updateService);

        var exitCode = await runner.RunAsync(CommandLineOptions.Parse("update"), PayloadDirectory, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal(layout.GetVersionExecutablePath("0.2.0"), request.FileName);
        Assert.Contains("--apply-update", request.Arguments);
        Assert.True(request.Detached);
    }

    [Fact]
    public async Task RunAsync_Update_WhenNoNewerRelease_ReturnsSuccessWithoutStaging()
    {
        var release = new ReleaseInfo
        {
            TagName = "v0.1.0",
            Assets =
            [
                new ReleaseAsset
                {
                    Name = "Phantom.Workspaces-win-x64.zip",
                    DownloadUrl = "https://example/win-x64.zip",
                },
            ],
        };
        var fs = new InMemoryFileSystem();
        var layout = new InstallLayout(fs, AppRoot);
        fs.CreateDirectory(layout.GetVersionDirectory("0.1.0"));
        layout.RepointCurrent("0.1.0");
        var downloader = new FakeUpdateDownloader(fs, new byte[] { 1, 2 });
        var updateService = new UpdateService(
            new FakeReleaseSource(release), downloader, new FakeArchiveExtractor(fs),
            fs, layout, runningVersion: "0.1.0", assetMoniker: "win-x64");

        var launcher = new FakeProcessLauncher();
        var startupTaskService = new StartupTaskService(new FakeStartupRegistration(), new FakeScheduledTasks(), layout.CurrentExecutablePath);
        var applyUpdateRunner = new ApplyUpdateRunner(
            layout, new FakeInstanceReleaseWaiter(released: true), new HealthGate(fs, layout), launcher);
        var runner = new ManagementModeRunner(
            layout, fs, new ManualClock(FixedInstant), launcher, startupTaskService, applyUpdateRunner, updateService);

        var exitCode = await runner.RunAsync(CommandLineOptions.Parse("update"), PayloadDirectory, "0.1.0");

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Empty(downloader.DownloadedTo);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public async Task RunAsync_Update_WhenUpdateServiceMissing_ReturnsFailure()
    {
        var harness = new Harness();

        var exitCode = await harness.Runner.RunAsync(CommandLineOptions.Parse("update"), PayloadDirectory, "0.1.0");

        Assert.Equal(ExitCode.GeneralFailure, exitCode);
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
