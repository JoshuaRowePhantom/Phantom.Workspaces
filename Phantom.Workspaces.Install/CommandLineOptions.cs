namespace Phantom.Workspaces.Install;

/// <summary>
/// The parsed command line. Parsing is pure and has no side effects, so it can be exhaustively
/// unit-tested in isolation. Construct instances via <see cref="Parse"/>.
/// </summary>
public sealed class CommandLineOptions
{
    private CommandLineOptions()
    {
    }

    /// <summary>The selected launch mode.</summary>
    public LaunchMode Mode { get; private init; }

    /// <summary>Whether <c>--silent</c> was supplied to <c>--install</c>.</summary>
    public bool Silent { get; private init; }

    /// <summary>Whether <c>--relaunch</c> was supplied to <c>--apply-update</c>.</summary>
    public bool Relaunch { get; private init; }

    /// <summary>Whether <c>--purge</c> was supplied to <c>--uninstall</c>.</summary>
    public bool Purge { get; private init; }

    /// <summary>The staged version directory passed to <c>--apply-update</c>.</summary>
    public string? ApplyUpdateDirectory { get; private init; }

    /// <summary>
    /// The install-root override (hidden <c>--install-root &lt;path&gt;</c>), applicable across
    /// modes so tests and <c>scripts\test-install.ps1</c> can run in a sandbox.
    /// </summary>
    public string? InstallRootOverride { get; private init; }

    /// <summary>Whether the command line parsed successfully.</summary>
    public bool IsValid { get; private init; }

    /// <summary>A human-readable description of why parsing failed, when <see cref="IsValid"/> is false.</summary>
    public string? Error { get; private init; }

    /// <summary>The exit code to return for this parse result.</summary>
    public ExitCode ExitCode { get; private init; }

    /// <summary>
    /// Parses <paramref name="arguments"/> into a <see cref="CommandLineOptions"/> without any
    /// side effects. Unknown arguments, conflicting modes, or missing required values produce an
    /// invalid result with <see cref="ExitCode.BadArguments"/>.
    /// </summary>
    public static CommandLineOptions Parse(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        LaunchMode? mode = null;
        var silent = false;
        var relaunch = false;
        var purge = false;
        string? applyUpdateDirectory = null;
        string? installRootOverride = null;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--install":
                    if (!TrySetMode(ref mode, LaunchMode.Install, out var installError))
                    {
                        return Invalid(installError);
                    }

                    break;

                case "--startup":
                    if (!TrySetMode(ref mode, LaunchMode.Startup, out var startupError))
                    {
                        return Invalid(startupError);
                    }

                    break;

                case "--minimized":
                    if (!TrySetMode(ref mode, LaunchMode.Minimized, out var minimizedError))
                    {
                        return Invalid(minimizedError);
                    }

                    break;

                case "--uninstall":
                    if (!TrySetMode(ref mode, LaunchMode.Uninstall, out var uninstallError))
                    {
                        return Invalid(uninstallError);
                    }

                    break;

                case "--apply-update":
                    if (!TrySetMode(ref mode, LaunchMode.ApplyUpdate, out var applyError))
                    {
                        return Invalid(applyError);
                    }

                    if (index + 1 >= arguments.Length)
                    {
                        return Invalid("--apply-update requires a staged version directory.");
                    }

                    applyUpdateDirectory = arguments[++index];
                    break;

                case "--help":
                case "-h":
                    if (!TrySetMode(ref mode, LaunchMode.Help, out var helpError))
                    {
                        return Invalid(helpError);
                    }

                    break;

                case "--silent":
                    silent = true;
                    break;

                case "--relaunch":
                    relaunch = true;
                    break;

                case "--purge":
                    purge = true;
                    break;

                case "--install-root":
                    if (index + 1 >= arguments.Length)
                    {
                        return Invalid("--install-root requires a path.");
                    }

                    installRootOverride = arguments[++index];
                    break;

                default:
                    return Invalid($"Unknown argument: {argument}");
            }
        }

        var resolvedMode = mode ?? LaunchMode.Gui;
        if (silent && resolvedMode != LaunchMode.Install)
        {
            return Invalid("--silent is only valid with --install.");
        }

        if (relaunch && resolvedMode != LaunchMode.ApplyUpdate)
        {
            return Invalid("--relaunch is only valid with --apply-update.");
        }

        if (purge && resolvedMode != LaunchMode.Uninstall)
        {
            return Invalid("--purge is only valid with --uninstall.");
        }

        return new CommandLineOptions
        {
            Mode = resolvedMode,
            Silent = silent,
            Relaunch = relaunch,
            Purge = purge,
            ApplyUpdateDirectory = applyUpdateDirectory,
            InstallRootOverride = installRootOverride,
            IsValid = true,
            Error = null,
            ExitCode = ExitCode.Success,
        };
    }

    private static bool TrySetMode(ref LaunchMode? mode, LaunchMode requested, out string error)
    {
        if (mode is { } existing && existing != requested)
        {
            error = $"Conflicting modes: {existing} and {requested}.";
            return false;
        }

        mode = requested;
        error = string.Empty;
        return true;
    }

    private static CommandLineOptions Invalid(string error)
    {
        return new CommandLineOptions
        {
            Mode = LaunchMode.Help,
            IsValid = false,
            Error = error,
            ExitCode = ExitCode.BadArguments,
        };
    }
}
