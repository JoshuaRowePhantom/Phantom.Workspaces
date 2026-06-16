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
            "  Phantom.Workspaces <path>                  Open a local Git-backed repository at <path>.",
            "  Phantom.Workspaces <https-url>             Connect to a remote Phantom.Workspaces server.",
            "  Phantom.Workspaces --data-store mongodb ... Open a MongoDB-backed repository (see options).",
            "  Phantom.Workspaces /?                       Show this help.",
            string.Empty,
            "MongoDB options (used with --data-store mongodb):",
            "  --data-store mongodb                       Required. Selects the MongoDB data store.",
            "  --mongodb-container-name <name>            Required. The MongoDB container name.",
            "  --mongodb-root-collection-name <name>      Required. The root entity collection name.",
            "  --mongodb-data-directory <path>            Optional. Defaults to",
            "                                             <user home>/Phantom.Workspaces/Mongo.",
            "  --mongodb-database-name <name>             Optional. Defaults to 'phantom-workspaces'.",
            "  --mongodb-host-port <port>                 Optional. The host port mapped to MongoDB.",
            string.Empty,
            "Help flags: /?, -?, /h, -h, /help, --help");
    }
}
