using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Containers;

public sealed class WindowsDockerDesktopEngine : DockerDesktopEngine
{
    private readonly IDockerCommandRunner _commandRunner;

    internal IDockerCommandRunner CommandRunner => _commandRunner;

    /// <summary>
    /// Reserved for tests. Wires a <see cref="NullLogger{T}"/> so docker output is discarded;
    /// production must use a logger-bearing constructor so stdout/stderr are surfaced (issue #1373).
    /// </summary>
    internal WindowsDockerDesktopEngine()
        : this(new DockerCommandRunner(NullLogger<DockerCommandRunner>.Instance))
    {
    }

    public WindowsDockerDesktopEngine(ILogger<DockerCommandRunner> logger)
        : this(new DockerCommandRunner(logger))
    {
    }

    public WindowsDockerDesktopEngine(IDockerCommandRunner commandRunner)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    public override async ValueTask<bool> UsableAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _commandRunner
                .RunAsync(["info"], cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public override async ValueTask CreateAsync(
        ContainerDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.ContainerName))
        {
            throw new ArgumentException("Container name is required.", nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.ImageName))
        {
            throw new ArgumentException("Image name is required.", nameof(definition));
        }

        if (await ContainerExistsAsync(definition.ContainerName, cancellationToken).ConfigureAwait(false))
        {
            await DestroyAsync(definition.ContainerName, cancellationToken).ConfigureAwait(false);
        }

        var arguments = new List<string>
        {
            "create",
            "--name",
            definition.ContainerName,
            "--network",
            definition.NetworkType.ToString().ToLowerInvariant(),
        };

        foreach (var environmentVariable in definition.EnvironmentVariables)
        {
            arguments.Add("-e");
            arguments.Add($"{environmentVariable.Key}={environmentVariable.Value}");
        }

        foreach (var mount in definition.Mounts)
        {
            var mountArgument = $"type=bind,source={mount.Source},target={mount.Target}";
            if (mount.ReadOnly)
            {
                mountArgument += ",readonly";
            }

            arguments.Add("--mount");
            arguments.Add(mountArgument);
        }

        foreach (var portMapping in definition.PortMappings)
        {
            arguments.Add("-p");
            arguments.Add($"{portMapping.SourcePort}:{portMapping.TargetPort}");
        }

        arguments.Add(definition.ImageName);

        await RunAndEnsureSuccessAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask StartAsync(
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ValidateContainerName(containerName);
        return RunAndEnsureSuccessAsync(["start", containerName], cancellationToken);
    }

    public override ValueTask StopAsync(
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ValidateContainerName(containerName);
        return RunAndEnsureSuccessAsync(["stop", containerName], cancellationToken);
    }

    public override ValueTask DestroyAsync(
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ValidateContainerName(containerName);
        return RunAndEnsureSuccessAsync(["rm", "-f", containerName], cancellationToken);
    }

    private async ValueTask<bool> ContainerExistsAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner
            .RunAsync(["container", "inspect", containerName], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async ValueTask RunAndEnsureSuccessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner
            .RunAsync(arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            return;
        }

        var failureDetails = string.Join(
            Environment.NewLine,
            new[] { result.StandardOut, result.StandardError }
                .Where(stream => !string.IsNullOrWhiteSpace(stream)));

        throw new InvalidOperationException(
            $"Docker command failed: docker {string.Join(' ', arguments)}{Environment.NewLine}" +
            failureDetails.TrimEnd());
    }

    private static void ValidateContainerName(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new ArgumentException("Container name is required.", nameof(containerName));
        }
    }
}
