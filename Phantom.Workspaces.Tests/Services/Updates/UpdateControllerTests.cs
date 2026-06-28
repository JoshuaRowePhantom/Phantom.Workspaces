using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Tests.Updates;

public sealed class UpdateControllerTests
{
    private const string InstallRoot = @"C:\app";

    [Fact]
    public async Task CheckForUpdatesAsync_ReportsAvailabilityAndRaisesEvent()
    {
        var harness = new Harness(runningVersion: "1.0.0", latestTag: "v1.2.0");

        UpdateAvailability? raised = null;
        harness.Controller.UpdateAvailabilityChanged += (_, availability) => raised = availability;

        var result = await harness.Controller.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("1.2.0", harness.Controller.LatestAvailableVersion);
        Assert.NotNull(raised);
        Assert.True(raised!.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NoNewerRelease_ReportsNoUpdate()
    {
        var harness = new Harness(runningVersion: "2.0.0", latestTag: "v1.2.0");

        var result = await harness.Controller.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task DownloadInstallAndRelaunchAsync_LaunchesStagedExeAndRequestsShutdown()
    {
        var harness = new Harness(runningVersion: "1.0.0", latestTag: "v1.2.0");
        await harness.Controller.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        await harness.Controller.DownloadInstallAndRelaunchAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(harness.LaunchedRequest);
        Assert.Equal(harness.Layout.GetVersionExecutablePath("1.2.0"), harness.LaunchedRequest!.FileName);
        Assert.Equal(
            new[] { "--apply-update", harness.Layout.GetVersionDirectory("1.2.0"), "--relaunch" },
            harness.LaunchedRequest.Arguments);
        Assert.True(harness.ShutdownRequested);
    }

    [Fact]
    public async Task DownloadInstallAndRelaunchAsync_WithoutAvailableUpdate_DoesNothing()
    {
        var harness = new Harness(runningVersion: "1.0.0", latestTag: "v1.2.0");

        await harness.Controller.DownloadInstallAndRelaunchAsync(TestContext.Current.CancellationToken);

        Assert.Null(harness.LaunchedRequest);
        Assert.False(harness.ShutdownRequested);
    }

    [Fact]
    public void SetRunAtStartup_RegistersAndUnregistersScheduledTask()
    {
        var harness = new Harness(runningVersion: "1.0.0", latestTag: "v1.0.0");

        harness.Controller.SetRunAtStartup(true);
        Assert.True(harness.Controller.IsRunAtStartupEnabled);

        harness.Controller.SetRunAtStartup(false);
        Assert.False(harness.Controller.IsRunAtStartupEnabled);
    }

    private sealed class Harness
    {
        public Harness(string runningVersion, string latestTag)
        {
            var release = new ReleaseInfo
            {
                TagName = latestTag,
                Assets =
                [
                    new ReleaseAsset
                    {
                        Name = "Phantom.Workspaces-win-x64.zip",
                        DownloadUrl = "https://example/win-x64.zip",
                    },
                ],
            };

            var releaseSource = new Mock<IReleaseSource>();
            releaseSource
                .Setup(source => source.GetReleasesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { release });

            var downloader = new Mock<IUpdateDownloader>();
            downloader
                .Setup(d => d.DownloadAsync(It.IsAny<ReleaseAsset>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var extractor = new Mock<IArchiveExtractor>();
            var fileSystem = new Mock<IFileSystem> { DefaultValue = DefaultValue.Empty };

            this.Layout = new InstallLayout(fileSystem.Object, InstallRoot);

            var updateService = new UpdateService(
                releaseSource.Object,
                downloader.Object,
                extractor.Object,
                fileSystem.Object,
                this.Layout,
                runningVersion,
                "win-x64");

            var scheduledTasks = new InMemoryScheduledTasks();
            var startupTaskService = new StartupTaskService(scheduledTasks, this.Layout.CurrentExecutablePath);

            var processLauncher = new Mock<IProcessLauncher>();
            processLauncher
                .Setup(launcher => launcher.Start(It.IsAny<ProcessStartRequest>()))
                .Callback<ProcessStartRequest>(request => this.LaunchedRequest = request)
                .Returns(Mock.Of<IProcessHandle>());

            this.Controller = new UpdateController(
                updateService,
                startupTaskService,
                this.Layout,
                processLauncher.Object,
                runningVersion,
                AutomaticUpdateMode.NotifyOnly,
                installRootOverride: null,
                requestShutdown: () => this.ShutdownRequested = true);
        }

        public InstallLayout Layout { get; }

        public UpdateController Controller { get; }

        public ProcessStartRequest? LaunchedRequest { get; private set; }

        public bool ShutdownRequested { get; private set; }
    }

    private sealed class InMemoryScheduledTasks : IScheduledTasks
    {
        private readonly HashSet<string> tasks = new(StringComparer.OrdinalIgnoreCase);

        public bool Exists(string taskName) => this.tasks.Contains(taskName);

        public void Register(ScheduledTaskDefinition definition) => this.tasks.Add(definition.TaskName);

        public void Unregister(string taskName) => this.tasks.Remove(taskName);
    }
}
