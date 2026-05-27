using System.ComponentModel;
using System.Diagnostics;

namespace Phantom.Workspaces.Containers;

public readonly record struct DockerCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IDockerCommandRunner
{
    ValueTask<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class DockerCommandRunner : IDockerCommandRunner
{
    public async ValueTask<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start docker process.");
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);

            return new DockerCommandResult(
                process.ExitCode,
                standardOutput,
                standardError);
        }
        catch (Win32Exception ex)
        {
            return new DockerCommandResult(
                -1,
                string.Empty,
                ex.Message);
        }
    }
}
