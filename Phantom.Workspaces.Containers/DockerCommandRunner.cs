using System.ComponentModel;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<DockerCommandRunner> _logger;
    private readonly string _command;

    internal ILogger<DockerCommandRunner> Logger => _logger;

    public DockerCommandRunner(ILogger<DockerCommandRunner> logger)
        : this(logger, "docker")
    {
    }

    internal DockerCommandRunner(ILogger<DockerCommandRunner> logger, string command)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public async ValueTask<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return await ProcessRunner.RunAndLogAsync(
                new RunProcessParameters(
                    Command: _command,
                    Arguments: arguments),
                _logger,
                operationDescription: $"docker {string.Join(' ', arguments)}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, ex.Message);
        }
    }
}
