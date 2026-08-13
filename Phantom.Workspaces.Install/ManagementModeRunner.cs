namespace Phantom.Workspaces.Install;

/// <summary>
/// Executes the headless management modes (<c>--install</c>, <c>--apply-update</c>,
/// <c>--uninstall</c>) and returns an <see cref="ExitCode"/>. These modes run before/instead of
/// the normal GUI launch; the entry point dispatches everything else (normal/startup/minimized/
/// help) to the Avalonia app. Keeping the orchestration here makes it unit-testable with fakes.
/// </summary>
public sealed class ManagementModeRunner
{
    private readonly InstallLayout layout;
    private readonly IFileSystem fileSystem;
    private readonly IClock clock;
    private readonly IProcessLauncher processLauncher;
    private readonly StartupTaskService startupTaskService;
    private readonly ApplyUpdateRunner applyUpdateRunner;

    /// <summary>Creates the runner over its collaborators.</summary>
    public ManagementModeRunner(
        InstallLayout layout,
        IFileSystem fileSystem,
        IClock clock,
        IProcessLauncher processLauncher,
        StartupTaskService startupTaskService,
        ApplyUpdateRunner applyUpdateRunner)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentNullException.ThrowIfNull(startupTaskService);
        ArgumentNullException.ThrowIfNull(applyUpdateRunner);
        this.layout = layout;
        this.fileSystem = fileSystem;
        this.clock = clock;
        this.processLauncher = processLauncher;
        this.startupTaskService = startupTaskService;
        this.applyUpdateRunner = applyUpdateRunner;
    }

    /// <summary>Whether <paramref name="mode"/> is a headless management mode this runner handles.</summary>
    public static bool IsManagementMode(LaunchMode mode)
        => mode is LaunchMode.Install or LaunchMode.ApplyUpdate or LaunchMode.Uninstall;

    /// <summary>
    /// Runs the management mode described by <paramref name="options"/>. <paramref name="payloadDirectory"/>
    /// is the unmanaged source payload (the directory of the running executable) and
    /// <paramref name="version"/> is this build's version.
    /// </summary>
    public async Task<ExitCode> RunAsync(
        CommandLineOptions options,
        string payloadDirectory,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
        {
            return options.ExitCode;
        }

        return options.Mode switch
        {
            LaunchMode.Install => this.RunInstall(options, payloadDirectory, version),
            LaunchMode.ApplyUpdate => await this.RunApplyUpdateAsync(options, cancellationToken).ConfigureAwait(false),
            LaunchMode.Uninstall => this.RunUninstall(),
            _ => throw new InvalidOperationException($"{options.Mode} is not a management mode."),
        };
    }

    private ExitCode RunInstall(CommandLineOptions options, string payloadDirectory, string version)
    {
        try
        {
            this.layout.Bootstrap(payloadDirectory, version, this.clock.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ExitCode.BootstrapFailure;
        }

        // Register the per-user logon task pointing at app\current\Phantom.Workspaces.exe so the
        // app auto-runs on future logins. Best-effort — a failure here should not fail the install
        // (bits are already on disk; the user can enable startup from Settings later). Applies to
        // both silent and interactive installs — --silent means "no interactive installer UI", not
        // "don't register startup" (#1289).
        try
        {
            this.startupTaskService.Enable();
        }
        catch (Exception)
        {
            // Swallow: install still succeeded.
        }

        // Launch the freshly-installed managed app. Use --startup so the app honors the same
        // launch-mode semantics the logon task will use every day thereafter. Applies to both
        // silent and interactive installs (#1289). Best-effort — a launch failure does not fail
        // the install; the payload is already installed.
        try
        {
            this.processLauncher.Start(new ProcessStartRequest
            {
                FileName = this.layout.CurrentExecutablePath,
                Arguments = new[] { StartupTaskService.StartupArgument },
            });
        }
        catch (Exception)
        {
            // Swallow: install still succeeded.
        }

        return ExitCode.Success;
    }

    private async Task<ExitCode> RunApplyUpdateAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApplyUpdateDirectory))
        {
            return ExitCode.BadArguments;
        }

        return await this.applyUpdateRunner
            .RunAsync(options.ApplyUpdateDirectory, options.Relaunch, cancellationToken)
            .ConfigureAwait(false);
    }

    private ExitCode RunUninstall()
    {
        try
        {
            this.startupTaskService.Disable();

            // Remove the `current` directory link first. A recursive delete of the app root would
            // otherwise follow the junction into the active version directory, corrupting the
            // delete. Deleting the link alone removes the reparse point, not the target.
            if (this.fileSystem.DirectoryExists(this.layout.CurrentLinkPath))
            {
                this.fileSystem.DeleteDirectory(this.layout.CurrentLinkPath, recursive: false);
            }

            if (this.fileSystem.DirectoryExists(this.layout.AppRoot))
            {
                this.fileSystem.DeleteDirectory(this.layout.AppRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ExitCode.GeneralFailure;
        }

        return ExitCode.Success;
    }
}
