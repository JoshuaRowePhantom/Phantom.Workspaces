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

    internal static ProcessStartInfo BuildProcessStartInfo(string cliPath, string arguments)
    {
        if (OperatingSystem.IsWindows() && cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo("cmd.exe", $"/c \"{cliPath}\" {arguments}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }

        return new ProcessStartInfo(cliPath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    internal static RunProcessParameters BuildRunProcessParameters(
        string cliPath,
        string arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var argTokens = arguments.Split(' ');
        if (OperatingSystem.IsWindows() && cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new RunProcessParameters("cmd.exe", ["/c", cliPath, ..argTokens], Timeout: timeout, EnvironmentVariables: environmentVariables);
        }

        return new RunProcessParameters(cliPath, argTokens, Timeout: timeout, EnvironmentVariables: environmentVariables);
    }
}
