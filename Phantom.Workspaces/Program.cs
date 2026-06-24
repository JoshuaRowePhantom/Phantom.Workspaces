using Avalonia;
using System;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces;

class Program
{
    public static string[] StartupArguments { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// The single-instance guard owned by the primary instance, or <see langword="null"/> when this
    /// launch is not subject to single-instance arbitration (for example a help request). The GUI
    /// subscribes to its activation events to restore the window when a duplicate launch occurs.
    /// </summary>
    public static SingleInstanceGuard? InstanceGuard { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        StartupArguments = args;

        var managementExitCode = ManagementModeDispatcher.TryRun(args);
        if (managementExitCode is { } exitCode)
        {
            Environment.Exit(exitCode);
            return;
        }

        // Single-instance per configuration file: a second launch pointed at the same configuration
        // file signals the running instance to activate and then exits, while launches pointed at
        // different configuration files coexist. This lets multiple instances run on one computer
        // for testing simply by passing different configuration file paths.
        if (!CommandLineOptions.IsHelpRequested(args))
        {
            CommandLineOptions.TryGetConfigurationFilePath(args, out var configurationFilePath);
            var guard = SingleInstanceGuard.Acquire(configurationFilePath);
            if (!guard.IsPrimaryInstance)
            {
                guard.SignalActivationAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                guard.Dispose();
                return;
            }

            InstanceGuard = guard;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceGuard?.Dispose();
            InstanceGuard = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
