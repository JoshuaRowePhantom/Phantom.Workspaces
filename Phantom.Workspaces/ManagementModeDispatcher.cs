using System;
using System.Linq;
using System.Reflection;
using InstallCommandLineOptions = Phantom.Workspaces.Install.CommandLineOptions;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces;

/// <summary>
/// Bridges the process entry point to the headless management modes (<c>--install</c>,
/// <c>--apply-update</c>, <c>--uninstall</c>) implemented in <see cref="ManagementModeRunner"/>.
/// Only those explicit flags are intercepted; every other invocation (normal launch, a positional
/// configuration-file path, <c>--help</c>, <c>--startup</c>, <c>--minimized</c>) falls through to
/// the Avalonia application unchanged.
/// </summary>
internal static class ManagementModeDispatcher
{
    private static readonly string[] ManagementFlags = { "--install", "--apply-update", "--uninstall" };

    /// <summary>
    /// Runs a management mode if <paramref name="arguments"/> requests one, returning the process
    /// exit code. Returns <see langword="null"/> when no management flag is present, signalling the
    /// caller to continue with the normal GUI launch.
    /// </summary>
    public static int? TryRun(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.Any(argument => ManagementFlags.Contains(argument, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        var options = InstallCommandLineOptions.Parse(arguments);
        if (!options.IsValid)
        {
            if (!string.IsNullOrEmpty(options.Error))
            {
                Console.Error.WriteLine(options.Error);
            }

            return (int)options.ExitCode;
        }

        var installRoot = InstallRootResolver.Resolve(options.InstallRootOverride);
        var fileSystem = new RealFileSystem();
        var layout = new InstallLayout(fileSystem, installRoot);
        var clock = new SystemClock();
        var processLauncher = new RealProcessLauncher();
        var startupTaskService = new StartupTaskService(new RealScheduledTasks(), layout.CurrentExecutablePath);
        var healthGate = new HealthGate(fileSystem, layout);
        var releaseWaiter = new RealInstanceReleaseWaiter(configFilePath: null);
        var applyUpdateRunner = new ApplyUpdateRunner(layout, releaseWaiter, healthGate, processLauncher);

        var runner = new ManagementModeRunner(
            layout,
            fileSystem,
            clock,
            processLauncher,
            startupTaskService,
            applyUpdateRunner);

        var payloadDirectory = AppContext.BaseDirectory;
        var version = ResolveVersion();

        var exitCode = runner
            .RunAsync(options, payloadDirectory, version)
            .GetAwaiter()
            .GetResult();

        return (int)exitCode;
    }

    private static string ResolveVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "0.0.0";
        }

        // Strip the source-revision suffix (e.g. "0.1.0+abc123") that the SDK appends.
        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }
}
