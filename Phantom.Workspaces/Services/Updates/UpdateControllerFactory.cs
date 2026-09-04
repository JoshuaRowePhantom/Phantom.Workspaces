using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>
/// Builds the production <see cref="UpdateController"/> by wiring the real install/update seams
/// (filesystem, layout, GitHub release source, HTTP downloader, zip extractor, startup task) the
/// same way <see cref="ManagementModeDispatcher"/> does for headless modes. Kept separate from the
/// controller so the controller itself stays fully unit-testable with in-memory fakes.
/// </summary>
public static class UpdateControllerFactory
{
    /// <summary>
    /// Creates a controller for the running, installed application. <paramref name="requestShutdown"/>
    /// is invoked after an update is staged and the relaunch process is started, so the running
    /// process can exit and release the single-instance lock for the swap. Returns <c>null</c> when
    /// the process is not running from an install layout (e.g. a development <c>dotnet run</c>),
    /// where self-update does not apply.
    /// </summary>
    public static UpdateController? TryCreate(
        WorkspacesConfiguration configuration,
        Action requestShutdown,
        string? installRootOverride = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(requestShutdown);

        var fileSystem = new RealFileSystem();
        var installRoot = InstallRootResolver.Resolve(installRootOverride);
        var layout = new InstallLayout(fileSystem, installRoot);

        // Self-update only makes sense when running from the versioned install layout; a development
        // run from a build output directory has no 'current' junction to repoint.
        if (!IsRunningFromInstallLayout(layout))
        {
            return null;
        }

        var client = httpClient ?? new HttpClient();
        var releaseSource = new GitHubReleaseSource(client);
        var downloader = new HttpUpdateDownloader(client);
        var extractor = new ZipArchiveExtractor();
        var runningVersion = ResolveVersion();
        var updateService = new UpdateService(
            releaseSource,
            downloader,
            extractor,
            fileSystem,
            layout,
            runningVersion,
            ResolveAssetMoniker());
#pragma warning disable CA1416 // RealScheduledTasks/RegistryStartupRegistration are Windows-only; this path is only reached on Windows
        var startupTaskService = new StartupTaskService(
            new RegistryStartupRegistration(),
            new RealScheduledTasks(NullLogger<RealScheduledTasks>.Instance),
            layout.CurrentExecutablePath);
#pragma warning restore CA1416
        var processLauncher = new RealProcessLauncher();

        return new UpdateController(
            updateService,
            startupTaskService,
            layout,
            processLauncher,
            runningVersion,
            configuration.Update.Mode,
            installRootOverride,
            requestShutdown);
    }

    private static bool IsRunningFromInstallLayout(InstallLayout layout)
    {
        try
        {
            return layout.ResolveCurrentVersion() is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveAssetMoniker()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

    private static string ResolveVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "0.0.0";
        }

        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }
}
