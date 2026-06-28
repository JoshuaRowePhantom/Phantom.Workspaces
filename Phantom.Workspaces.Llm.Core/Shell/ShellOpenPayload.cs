using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// The JSON open-payload for a <c>"shell"</c> stream kind. Carries the start parameters for
/// the process spawned by <see cref="LocalShellStreamHandler"/>.
/// </summary>
internal sealed record ShellOpenPayload
{
    /// <summary>Run mode: <c>"pty"</c> (pseudo-terminal) or <c>"pipe"</c> (redirected stdio).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "pty";

    /// <summary>Executable to launch (e.g. <c>pwsh</c>, <c>bash</c>).</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>Arguments passed to <see cref="Command"/>.</summary>
    [JsonPropertyName("command-arguments")]
    public IReadOnlyList<string> CommandArguments { get; init; } = [];

    /// <summary>Working directory for the child process; <see langword="null"/> inherits the current directory.</summary>
    [JsonPropertyName("working-directory")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Additional environment variables to set in the child process.</summary>
    [JsonPropertyName("environment")]
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Initial terminal width in columns (pty mode).</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; init; } = 120;

    /// <summary>Initial terminal height in rows (pty mode).</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; } = 30;
}
