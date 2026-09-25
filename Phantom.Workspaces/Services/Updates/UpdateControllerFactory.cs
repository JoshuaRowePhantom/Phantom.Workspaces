using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    /// process can exit and release the single-instance lock for the swap. Returns a result with
    /// no controller when the process is not running from an install layout (e.g. a development
    /// <c>dotnet run</c>), where self-update does not apply. The returned reason distinguishes this
    /// from a damaged installed layout.
    /// </summary>
    public static UpdateControllerCreationResult TryCreate(
        WorkspacesConfiguration configuration,
        Action requestShutdown,
        ILoggerFactory loggerFactory,
        string? installRootOverride = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(requestShutdown);

        var fileSystem = new RealFileSystem();
        var installRoot = InstallRootResolver.Resolve(installRootOverride);
        var layout = new InstallLayout(fileSystem, installRoot);

        var executablePath = Environment.ProcessPath ?? string.Empty;
        if (!IsManagedExecutable(layout, executablePath))
        {
            return new UpdateControllerCreationResult(null, GetUnavailableReason(layout, executablePath, currentVersion: null));
        }

        string? currentVersion;
        try
        {
            currentVersion = layout.ResolveCurrentVersion();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loggerFactory.CreateLogger(typeof(UpdateControllerFactory).FullName!).LogError(
                exception, "Unable to inspect the installed update layout at {InstallRoot}", installRoot);
            return new UpdateControllerCreationResult(null, InstalledLayoutError);
        }

        var unavailableReason = GetUnavailableReason(layout, executablePath, currentVersion);
        if (unavailableReason is not null)
        {
            loggerFactory.CreateLogger(typeof(UpdateControllerFactory).FullName!).LogError(
                "The running executable is in an installed layout but the current version link is missing at {InstallRoot}", installRoot);
            return new UpdateControllerCreationResult(null, unavailableReason);
        }

        string assetMoniker;
        try
        {
            assetMoniker = ResolveAssetMoniker(RuntimeInformation.ProcessArchitecture);
        }
        catch (PlatformNotSupportedException exception)
        {
            loggerFactory.CreateLogger(typeof(UpdateControllerFactory).FullName!).LogError(
                exception, "Updates are not supported on this architecture");
            return new UpdateControllerCreationResult(null, $"Updates unavailable: {exception.Message}");
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
            assetMoniker);
#pragma warning disable CA1416 // RealScheduledTasks/RegistryStartupRegistration are Windows-only; this path is only reached on Windows
        var startupTaskService = new StartupTaskService(
            new RegistryStartupRegistration(),
            new RealScheduledTasks(loggerFactory.CreateLogger<RealScheduledTasks>()),
            layout.CurrentExecutablePath);
#pragma warning restore CA1416
        var processLauncher = new RealProcessLauncher();

        return new UpdateControllerCreationResult(new UpdateController(
            updateService,
            startupTaskService,
            layout,
            processLauncher,
            runningVersion,
            configuration.Update.Mode,
            installRootOverride,
            requestShutdown), null);
    }

    private const string InstalledLayoutError =
        "Updates unavailable: the installed update layout is missing or cannot be read. Reinstall Phantom Workspaces to repair it.";

    internal static string? GetUnavailableReason(InstallLayout layout, string executablePath, string? currentVersion)
        => !IsManagedExecutable(layout, executablePath)
            ? "Updates not available in this run/build. Install Phantom Workspaces to enable checks and installation."
            : currentVersion is null ? InstalledLayoutError : null;

    private static bool IsManagedExecutable(InstallLayout layout, string executablePath)
        => !string.IsNullOrWhiteSpace(executablePath)
            && string.Equals(Path.GetFileName(executablePath), InstallLayout.ApplicationExecutableName, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(layout.CurrentExecutablePath), StringComparison.OrdinalIgnoreCase)
                || layout.IsManagedExecutable(executablePath));

    internal static string ResolveAssetMoniker(Architecture architecture)
        => architecture == Architecture.Arm64
            ? throw new PlatformNotSupportedException(
                "Microsoft MXC-backed Phantom.Workspaces releases currently support only win-x64; ARM64 updates are unavailable.")
            : "win-x64";

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

/// <summary>The controller and explicit capability result of probing the running installation.</summary>
public sealed record UpdateControllerCreationResult(UpdateController? Controller, string? UnavailableReason);
