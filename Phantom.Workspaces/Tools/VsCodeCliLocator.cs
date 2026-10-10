using System.Diagnostics;

namespace Phantom.Workspaces.Tools;



internal static class VsCodeCliLocator
{
    public static string ResolveDefaultCliPath()
        => ResolveDefaultCliPath(File.Exists);

    internal static string ResolveDefaultCliPath(Func<string, bool> fileExists)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in GetWindowsCandidatePaths())
            {
                if (fileExists(candidate))
                    return candidate;
            }

            var found = TryFindViaWhereExe();
            if (found is not null)
                return found;
        }

        return "code";
    }

    internal static IReadOnlyList<string> GetWindowsCandidatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        return
        [
            Path.Combine(localAppData, "Programs", "Microsoft VS Code", "bin", "code.cmd"),
            Path.Combine(programFiles, "Microsoft VS Code", "bin", "code.cmd"),
            Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd"),
            Path.Combine(programFiles, "Microsoft VS Code Insiders", "bin", "code-insiders.cmd"),
        ];
    }

    private static string? TryFindViaWhereExe()
    {
        var psi = new ProcessStartInfo("where.exe", "code.cmd")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return output.Split('\n')[0].Trim();

        return null;
    }

    internal static ProcessStartInfo BuildProcessStartInfo(string cliPath, IReadOnlyList<string> arguments)
    {
        var parameters = BuildRunProcessParameters(cliPath, arguments);
        var startInfo = new ProcessStartInfo(parameters.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (parameters.CommandInterpreterArguments is { } commandLine)
            startInfo.Arguments = commandLine;
        else
            foreach (var argument in parameters.Arguments)
                startInfo.ArgumentList.Add(argument);
        if (parameters.EnvironmentVariables is not null)
            foreach (var (key, value) in parameters.EnvironmentVariables)
                startInfo.Environment[key] = value;
        return startInfo;
    }

    internal static RunProcessParameters BuildRunProcessParameters(
        string cliPath,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (OperatingSystem.IsWindows() && cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var environment = environmentVariables is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(environmentVariables);
            var prefix = "PHANTOM_CODE_" + Guid.NewGuid().ToString("N");
            var operands = new[] { cliPath }.Concat(arguments).ToArray();
            var references = new List<string>();
            for (var index = 0; index < operands.Length; index++)
            {
                var operand = operands[index];
                if (operand.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
                    throw new ArgumentException("Windows CLI operands cannot contain quotes or control characters.", nameof(arguments));
                if (operand.Length == 0)
                {
                    references.Add("\"\"");
                    continue;
                }
                var name = $"{prefix}_{index}";
                // cmd expands environment references once, inside quotes. Keep user text out of
                // the shell syntax, disable delayed expansion, and retain CRT trailing slashes
                // when a CLI batch shim forwards %* to its executable.
                var trailingSlashes = operand.Length - operand.TrimEnd('\\').Length;
                environment[name] = operand + new string('\\', trailingSlashes);
                references.Add($"\"%{name}%\"");
            }
            return new RunProcessParameters("cmd.exe", [], Timeout: timeout, EnvironmentVariables: environment)
            {
                CommandInterpreterArguments = "/d /v:off /s /c \"" + string.Join(' ', references) + "\"",
            };
        }

        return new RunProcessParameters(cliPath, arguments, Timeout: timeout, EnvironmentVariables: environmentVariables);
    }
}
