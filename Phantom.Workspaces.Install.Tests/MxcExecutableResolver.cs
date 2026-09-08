using System.Text;

namespace Phantom.Workspaces.Install.Tests;

internal static class MxcExecutableResolver
{
    internal sealed record ResolvedCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        string? ArgumentString = null);

    internal static ResolvedCommand Resolve(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> searchDirectories,
        IReadOnlyList<string> pathExtensions,
        bool isWindows,
        string commandInterpreter,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(searchDirectories);
        ArgumentNullException.ThrowIfNull(pathExtensions);
        ArgumentNullException.ThrowIfNull(fileExists);

        var resolvedPath = FindExecutable(
            command,
            searchDirectories,
            pathExtensions,
            isWindows,
            fileExists);
        if (resolvedPath is null)
        {
            throw new FileNotFoundException(
                $"Could not resolve required executable '{command}' on PATH"
                + (isWindows ? "/PATHEXT." : "."),
                command);
        }

        var extension = Path.GetExtension(resolvedPath);
        if (isWindows
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(commandInterpreter))
            {
                throw new FileNotFoundException(
                    $"Could not launch command script '{resolvedPath}' because ComSpec is unavailable.",
                    resolvedPath);
            }

            return new ResolvedCommand(
                commandInterpreter,
                [],
                $"/d /s /c \"{BuildWindowsCommandLine(resolvedPath, arguments)}\"");
        }

        return new ResolvedCommand(resolvedPath, arguments);
    }

    private static string? FindExecutable(
        string command,
        IReadOnlyList<string> searchDirectories,
        IReadOnlyList<string> pathExtensions,
        bool isWindows,
        Func<string, bool> fileExists)
    {
        var hasDirectory = Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar);
        if (hasDirectory)
        {
            return FindCandidate(command, pathExtensions, isWindows, fileExists);
        }

        foreach (var directory in searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = FindCandidate(
                Path.Combine(directory.Trim().Trim('"'), command),
                pathExtensions,
                isWindows,
                fileExists);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindCandidate(
        string candidate,
        IReadOnlyList<string> pathExtensions,
        bool isWindows,
        Func<string, bool> fileExists)
    {
        if (!isWindows || Path.HasExtension(candidate))
        {
            return fileExists(candidate) ? candidate : null;
        }

        foreach (var extension in pathExtensions)
        {
            var normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
            var extendedCandidate = candidate + normalizedExtension;
            if (fileExists(extendedCandidate))
            {
                return extendedCandidate;
            }
        }

        return null;
    }

    private static string BuildWindowsCommandLine(string executable, IReadOnlyList<string> arguments)
        => string.Join(
            ' ',
            new[] { QuoteWindowsArgument(executable, forceQuotes: true) }
                .Concat(arguments.Select(argument => QuoteWindowsArgument(argument, forceQuotes: false))));

    private static string QuoteWindowsArgument(string argument, bool forceQuotes)
    {
        argument = argument.Replace("%", "^%", StringComparison.Ordinal);
        if (!forceQuotes
            && argument.Length > 0
            && !argument.Any(char.IsWhiteSpace)
            && argument.IndexOfAny(['"', '&', '|', '<', '>', '^', '(', ')', '%', '!']) < 0)
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append(character);
            }
            else
            {
                result.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
