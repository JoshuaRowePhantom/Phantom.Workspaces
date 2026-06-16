using System;
using System.Collections.Generic;

namespace Phantom.Workspaces;

/// <summary>
/// Parsing helpers for the Phantom.Workspaces command line, including detection of the help flag
/// and the help text describing available startup options.
/// </summary>
public static class CommandLineOptions
{
    private static readonly HashSet<string> HelpFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "/?",
        "-?",
        "/h",
        "-h",
        "/help",
        "--help",
    };

    /// <summary>
    /// Returns <see langword="true"/> if any argument requests the help screen (for example
    /// <c>/?</c>, <c>-h</c>, or <c>--help</c>).
    /// </summary>
    public static bool IsHelpRequested(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (var argument in arguments)
        {
            if (argument is not null && HelpFlags.Contains(argument.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the optional configuration file path argument (the first non-help argument). The
    /// command line accepts only a configuration file path; repository and remote-access settings are
    /// never supplied as command-line parameters.
    /// </summary>
    public static bool TryGetConfigurationFilePath(IReadOnlyList<string> arguments, out string? configurationFilePath)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument) || HelpFlags.Contains(argument.Trim()))
            {
                continue;
            }

            configurationFilePath = argument.Trim();
            return true;
        }

        configurationFilePath = null;
        return false;
    }

    /// <summary>
    /// Builds the help text describing how to start Phantom.Workspaces and which options are
    /// available.
    /// </summary>
    public static string GetHelpText()
    {
        return string.Join(
            Environment.NewLine,
            "Phantom.Workspaces",
            string.Empty,
            "Usage:",
            "  Phantom.Workspaces                         Start using the saved configuration, or run",
            "                                             the first-run setup wizard if none exists.",
            "  Phantom.Workspaces <config-file>           Start using the configuration file at the",
            "                                             given path.",
            "  Phantom.Workspaces /?                       Show this help.",
            string.Empty,
            "Repository and remote-access settings (data store, MongoDB container, endpoints, tokens,",
            "etc.) are configured only through the configuration file or the first-run setup wizard,",
            "never as command-line parameters.",
            string.Empty,
            "Help flags: /?, -?, /h, -h, /help, --help");
    }
}
