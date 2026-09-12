using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Resolves the executable to launch for a stdio MCP server on Windows (issue #1477). A user
/// command such as <c>npx</c> is searched on <c>PATH</c>/<c>PATHEXT</c>. A <c>.exe</c> is launched
/// directly; a <c>.cmd</c> or <c>.bat</c> is launched through <c>%ComSpec% /d /s /c</c> with the
/// original arguments quoted via <see cref="WindowsCommandLine"/> so npm-style shims retain their
/// original argv semantics without making every launch shell-based. Resolution failure is
/// explicit and happens before any child process is spawned.
/// </summary>
public static class StdioCommandResolver
{
    /// <summary>The resolved launch shape (executable + ordered argument list).</summary>
    public sealed record ResolvedCommand(string Executable, IReadOnlyList<string> Arguments);

    /// <summary>
    /// Search <c>PATH</c>/<c>PATHEXT</c> for <paramref name="command"/> and produce a
    /// <see cref="ResolvedCommand"/> that <see cref="IProcessExecutor"/> can run. Throws
    /// <see cref="FileNotFoundException"/> when no candidate exists.
    /// </summary>
    public static ResolvedCommand Resolve(string command, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);

        return ResolveOrNull(command, arguments)
            ?? throw new FileNotFoundException(
                $"Could not resolve executable '{command}' on PATH/PATHEXT.",
                command);
    }

    /// <summary>Non-throwing overload used by unit tests to inspect the resolver contract.</summary>
    public static ResolvedCommand? ResolveOrNull(string command, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);

        var candidate = FindOnPath(command);
        if (candidate is null)
            return null;

        var extension = Path.GetExtension(candidate).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comSpec))
                comSpec = "cmd.exe";

            var quoted = WindowsCommandLine.Build(candidate, arguments);
            return new ResolvedCommand(comSpec, ["/d", "/s", "/c", quoted]);
        }

        return new ResolvedCommand(candidate, arguments);
    }

    private static string? FindOnPath(string command)
    {
        // Absolute or rooted paths are honoured verbatim if they exist.
        if (Path.IsPathRooted(command))
        {
            if (File.Exists(command))
                return command;
            if (OperatingSystem.IsWindows())
            {
                foreach (var ext in GetPathExtensions())
                {
                    var candidate = command + ext;
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separators = new[] { Path.PathSeparator };
        var dirs = pathVar.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Try each PATH directory with each PATHEXT extension, preserving PATH order.
        var pathExtensions = OperatingSystem.IsWindows() ? GetPathExtensions() : [string.Empty];
        var hasExplicitExtension = !string.IsNullOrEmpty(Path.GetExtension(command));

        foreach (var directory in dirs)
        {
            var direct = Path.Combine(directory, command);
            if (hasExplicitExtension && File.Exists(direct))
                return direct;

            if (!hasExplicitExtension)
            {
                foreach (var extension in pathExtensions)
                {
                    var candidate = direct + extension;
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    private static string[] GetPathExtensions()
    {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        return [.. pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())];
    }
}
