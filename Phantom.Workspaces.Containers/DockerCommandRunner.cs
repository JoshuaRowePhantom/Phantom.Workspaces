using System.ComponentModel;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Containers;

public interface IDockerCommandRunner
{
    ValueTask<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class DockerCommandRunner : IDockerCommandRunner
{
    public async ValueTask<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return await ProcessRunner.RunProcessAsync(
                new RunProcessParameters(
                    Command: "docker",
                    Arguments: arguments),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, ex.Message);
        }
    }
}
