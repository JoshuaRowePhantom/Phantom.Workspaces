using Microsoft.Extensions.Logging;
using Phantom.Workspaces;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Containers.Tests;

public sealed class WindowsDockerDesktopEngineTests
{
    [Fact]
    public async Task CreateAsync_BuildsDockerCreateCommand()
    {
        var runner = new RecordingDockerCommandRunner();
        runner.Results.Enqueue(new ProcessResult(1, string.Empty, "missing", "missing"));
        var engine = new WindowsDockerDesktopEngine(runner);
        var definition = new ContainerDefinition
        {
            ContainerName = "sleep-container",
            ImageName = "test/sleep:latest",
            NetworkType = ContainerNetworkType.Bridge,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["SLEEP_SECONDS"] = "60",
            },
            Mounts =
            [
                new ContainerMountDefinition
                {
                    Source = "C:\\sleep-data",
                    Target = "/work",
                    ReadOnly = false,
                },
                new ContainerMountDefinition
                {
                    Source = "C:\\sleep-config",
                    Target = "/etc/sleep",
                    ReadOnly = true,
                },
            ],
            PortMappings =
            [
                new ContainerPortMappingDefinition
                {
                    SourcePort = 37017,
                    TargetPort = 27017,
                },
            ],
        };

        await engine.CreateAsync(definition);

        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal(["container", "inspect", "sleep-container"], runner.Commands[0]);

        var createCommand = runner.Commands[1];
        Assert.Equal("create", createCommand[0]);
        Assert.Contains("--name", createCommand);
        Assert.Contains("sleep-container", createCommand);
        Assert.Contains("--network", createCommand);
        Assert.Contains("bridge", createCommand);
        Assert.Contains("-e", createCommand);
        Assert.Contains("SLEEP_SECONDS=60", createCommand);
        Assert.Contains("--mount", createCommand);
        Assert.Contains("type=bind,source=C:\\sleep-data,target=/work", createCommand);
        Assert.Contains("type=bind,source=C:\\sleep-config,target=/etc/sleep,readonly", createCommand);
        Assert.Contains("-p", createCommand);
        Assert.Contains("37017:27017", createCommand);
        Assert.Equal("test/sleep:latest", createCommand[^1]);
    }

    [Fact]
    public async Task StartStopDestroyAsync_IssueDockerLifecycleCommands()
    {
        var runner = new RecordingDockerCommandRunner();
        var engine = new WindowsDockerDesktopEngine(runner);

        await engine.StartAsync("sleep-container");
        await engine.StopAsync("sleep-container");
        await engine.DestroyAsync("sleep-container");

        Assert.Equal(
            new[]
            {
                new[] { "start", "sleep-container" },
                new[] { "stop", "sleep-container" },
                new[] { "rm", "-f", "sleep-container" },
            },
            runner.Commands);
    }

    [Fact]
    public async Task UsableAsync_ReturnsTrue_WhenDockerInfoSucceeds()
    {
        var runner = new RecordingDockerCommandRunner();
        var engine = new WindowsDockerDesktopEngine(runner);

        var usable = await engine.UsableAsync();

        Assert.True(usable);
        Assert.Single(runner.Commands);
        Assert.Equal(["info"], runner.Commands[0]);
    }

    [Fact]
    public async Task UsableAsync_ReturnsFalse_WhenDockerInfoFails()
    {
        var runner = new RecordingDockerCommandRunner();
        runner.Results.Enqueue(new ProcessResult(1, string.Empty, "error", "error"));
        var engine = new WindowsDockerDesktopEngine(runner);

        var usable = await engine.UsableAsync();

        Assert.False(usable);
    }

    [Fact]
    public async Task CreateAsync_WhenContainerExists_RecreatesContainer()
    {
        var runner = new RecordingDockerCommandRunner();
        runner.Results.Enqueue(new ProcessResult(0, "exists", string.Empty, "exists"));
        runner.Results.Enqueue(new ProcessResult(0, string.Empty, string.Empty, string.Empty));
        runner.Results.Enqueue(new ProcessResult(0, string.Empty, string.Empty, string.Empty));
        var engine = new WindowsDockerDesktopEngine(runner);

        await engine.CreateAsync(new ContainerDefinition
        {
            ContainerName = "sleep-container",
            ImageName = "test/sleep:latest",
            NetworkType = ContainerNetworkType.Bridge,
            EnvironmentVariables = [],
            Mounts = [],
        });

        Assert.Equal(["container", "inspect", "sleep-container"], runner.Commands[0]);
        Assert.Equal(["rm", "-f", "sleep-container"], runner.Commands[1]);
        Assert.Equal("create", runner.Commands[2][0]);
    }

    [Fact]
    public async Task DockerCommandRunner_RunAsync_WhenCommandExitsNonZero_LogsWarning()
    {
        var logger = new FakeLogger<DockerCommandRunner>();
        var runner = new DockerCommandRunner(logger, "cmd.exe");

        await runner.RunAsync(["/c", "exit", "1"]);

        var entry = Assert.Single(logger.Logs);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    private sealed class RecordingDockerCommandRunner : IDockerCommandRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Queue<ProcessResult> Results { get; } = new();

        public ValueTask<ProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments.ToArray());

            if (Results.TryDequeue(out var result))
            {
                return ValueTask.FromResult(result);
            }

            return ValueTask.FromResult(new ProcessResult(0, string.Empty, string.Empty, string.Empty));
        }
    }

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
