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

    internal sealed record ResolverEnvironment(string Path, string PathExtensions, string ComSpec)
    {
        public static ResolverEnvironment Capture() => new(
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD",
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
    }

    /// <summary>
    /// Search <c>PATH</c>/<c>PATHEXT</c> for <paramref name="command"/> and produce a
    /// <see cref="ResolvedCommand"/> that <see cref="IProcessExecutor"/> can run. Throws
    /// <see cref="FileNotFoundException"/> when no candidate exists.
    /// </summary>
    public static ResolvedCommand Resolve(string command, IReadOnlyList<string> arguments)
        => Resolve(command, arguments, ResolverEnvironment.Capture());

    internal static ResolvedCommand Resolve(
        string command,
        IReadOnlyList<string> arguments,
        ResolverEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        return ResolveOrNull(command, arguments, environment)
            ?? throw new FileNotFoundException(
                $"Could not resolve executable '{command}' on PATH/PATHEXT.",
                command);
    }

    /// <summary>Non-throwing overload used by unit tests to inspect the resolver contract.</summary>
    public static ResolvedCommand? ResolveOrNull(string command, IReadOnlyList<string> arguments)
        => ResolveOrNull(command, arguments, ResolverEnvironment.Capture());

    internal static ResolvedCommand? ResolveOrNull(
        string command,
        IReadOnlyList<string> arguments,
        ResolverEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        var candidate = FindOnPath(command, environment);
        if (candidate is null)
            return null;

        var extension = Path.GetExtension(candidate).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            var quoted = WindowsCommandLine.Build(candidate, arguments);
            return new ResolvedCommand(environment.ComSpec, ["/d", "/s", "/c", quoted]);
        }

        return new ResolvedCommand(candidate, arguments);
    }

    private static string? FindOnPath(string command, ResolverEnvironment environment)
    {
        // Absolute or rooted paths are honoured verbatim if they exist.
        if (Path.IsPathRooted(command))
        {
            if (File.Exists(command))
                return command;
            if (OperatingSystem.IsWindows())
            {
                foreach (var ext in GetPathExtensions(environment.PathExtensions))
                {
                    var candidate = command + ext;
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        var separators = new[] { Path.PathSeparator };
        var dirs = environment.Path.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Try each PATH directory with each PATHEXT extension, preserving PATH order.
        var pathExtensions = OperatingSystem.IsWindows()
            ? GetPathExtensions(environment.PathExtensions)
            : [string.Empty];
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

    private static string[] GetPathExtensions(string pathExtensions)
    {
        return [.. pathExtensions
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())];
    }
}
