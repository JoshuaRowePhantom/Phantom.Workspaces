using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Tests for the Docker Desktop pre-flight path in <see cref="MongoDbConnectionBroker"/>
/// (issue #1299): when the container engine is not usable, launch Docker Desktop if installed,
/// poll <c>UsableAsync</c> with a bounded timeout, and raise actionable errors otherwise.
/// </summary>
public sealed class MongoDbConnectionBrokerDockerDesktopTests
{
    private static MongoDbContainerConnectionDefinition SampleConnection() => new()
    {
        ContainerName = "phantom-mongo-test",
        DataDirectory = Path.Combine(Path.GetTempPath(), "phantom-mongo-test-data"),
        DatabaseName = "test",
        CollectionName = "test",
        HostPort = 27017,
    };

    private static MongoDbConnectionBroker CreateBroker(
        FakeDockerEngine engine,
        FakeDockerDesktopLauncher launcher,
        FakeTimeProvider time,
        TimeSpan? readinessTimeout = null,
        TimeSpan? readinessPollInterval = null)
    {
        return new MongoDbConnectionBroker(
            containerEngine: engine,
            connectVerificationAttempts: 1,
            timeProvider: time,
            dockerDesktopLauncher: launcher,
            dockerReadinessTimeout: readinessTimeout ?? TimeSpan.FromSeconds(30),
            dockerReadinessPollInterval: readinessPollInterval ?? TimeSpan.FromSeconds(2));
    }

    private static async Task<Exception?> InvokeGetClientAndCatchAsync(
        MongoDbConnectionBroker broker,
        MongoDbContainerConnectionDefinition connection)
    {
        try
        {
            await broker.GetClientAsync(connection);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task EnsureContainerStarted_WhenDockerAlreadyUsable_DoesNotLaunchDockerDesktop()
    {
        var engine = new FakeDockerEngine { UsableResult = true };
        var launcher = new FakeDockerDesktopLauncher("C:\\Docker Desktop.exe");
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time);

        // GetClientAsync will still fail at the mongo-driver ping step (no real mongo), but the
        // Docker pre-flight succeeds first and never launches Docker Desktop. Assert on the
        // launcher / engine side effects, not on the final outcome.
        _ = await InvokeGetClientAndCatchAsync(broker, SampleConnection());

        Assert.Equal(0, launcher.LaunchCount);
        Assert.True(engine.StartCallCount >= 1);
    }

    [Fact]
    public async Task EnsureContainerStarted_WhenDockerNotUsableAndDesktopInstalled_LaunchesDockerDesktop()
    {
        var engine = new FakeDockerEngine { UsableResult = false };
        engine.OnPoll = () => { engine.UsableResult = true; };
        var launcher = new FakeDockerDesktopLauncher("C:\\Docker Desktop.exe");
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time,
            readinessTimeout: TimeSpan.FromSeconds(30),
            readinessPollInterval: TimeSpan.FromMilliseconds(10));

        var task = InvokeGetClientAndCatchAsync(broker, SampleConnection());
        // Advance the fake clock past the poll interval so the readiness loop wakes and re-probes.
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Yield();
        }
        _ = await task;

        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task EnsureContainerStarted_WhenDockerBecomesReady_StartsContainer()
    {
        var engine = new FakeDockerEngine { UsableResult = false };
        engine.OnPoll = () => { engine.UsableResult = true; };
        var launcher = new FakeDockerDesktopLauncher("C:\\Docker Desktop.exe");
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time,
            readinessTimeout: TimeSpan.FromSeconds(30),
            readinessPollInterval: TimeSpan.FromMilliseconds(10));

        var task = InvokeGetClientAndCatchAsync(broker, SampleConnection());
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Yield();
        }
        _ = await task;

        Assert.True(engine.StartCallCount >= 1);
    }

    [Fact]
    public async Task EnsureContainerStarted_WhenDockerDesktopNotInstalled_ThrowsActionableError()
    {
        var engine = new FakeDockerEngine { UsableResult = false };
        var launcher = new FakeDockerDesktopLauncher(installedExecutablePath: null);
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time);

        var exception = await InvokeGetClientAndCatchAsync(broker, SampleConnection());

        var ioe = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Docker Desktop is not installed", ioe.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Equal(0, engine.StartCallCount);
    }

    [Fact]
    public async Task EnsureContainerStarted_WhenEngineNeverBecomesReady_TimesOutWithActionableError()
    {
        var engine = new FakeDockerEngine { UsableResult = false };
        var launcher = new FakeDockerDesktopLauncher("C:\\Docker Desktop.exe");
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time,
            readinessTimeout: TimeSpan.FromMilliseconds(50),
            readinessPollInterval: TimeSpan.FromMilliseconds(10));

        var task = InvokeGetClientAndCatchAsync(broker, SampleConnection());
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Yield();
        }
        var exception = await task;

        var ioe = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("did not become ready", ioe.Message, StringComparison.Ordinal);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(0, engine.StartCallCount);
    }

    [Fact]
    public void MongoDbConnectionBroker_UsesLoggingDockerEngine_DoesNotUseNullLogger()
    {
        var logger = new FakeDockerCommandRunnerLogger();

        var broker = new MongoDbConnectionBroker(dockerCommandRunnerLogger: logger);

        var engine = Assert.IsType<WindowsDockerDesktopEngine>(broker.ContainerEngine);
        var runner = Assert.IsType<DockerCommandRunner>(engine.CommandRunner);
        Assert.Same(logger, runner.Logger);
        Assert.NotSame(Microsoft.Extensions.Logging.Abstractions.NullLogger<DockerCommandRunner>.Instance, runner.Logger);
    }

    private sealed class FakeDockerCommandRunnerLogger : Microsoft.Extensions.Logging.ILogger<DockerCommandRunner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class FakeDockerEngine : ContainerEngine
    {
        public bool UsableResult { get; set; }

        public int UsableCallCount { get; private set; }

        public int StartCallCount { get; private set; }

        public int CreateCallCount { get; private set; }

        // Invoked on every UsableAsync poll after the first (i.e. after the launcher has fired) to
        // let tests flip UsableResult based on wall-clock progress.
        public Action? OnPoll { get; set; }

        public override ValueTask<bool> UsableAsync(CancellationToken cancellationToken = default)
        {
            this.UsableCallCount++;
            if (this.UsableCallCount > 1)
            {
                this.OnPoll?.Invoke();
            }
            return new ValueTask<bool>(this.UsableResult);
        }

        public override ValueTask CreateAsync(ContainerDefinition definition, CancellationToken cancellationToken = default)
        {
            this.CreateCallCount++;
            return ValueTask.CompletedTask;
        }

        public override ValueTask StartAsync(string containerName, CancellationToken cancellationToken = default)
        {
            this.StartCallCount++;
            return ValueTask.CompletedTask;
        }

        public override ValueTask StopAsync(string containerName, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask DestroyAsync(string containerName, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeDockerDesktopLauncher : IDockerDesktopLauncher
    {
        public FakeDockerDesktopLauncher(string? installedExecutablePath)
        {
            this.InstalledExecutablePath = installedExecutablePath;
        }

        public string? InstalledExecutablePath { get; }

        public int LaunchCount { get; private set; }

        public void LaunchDockerDesktop()
        {
            this.LaunchCount++;
        }
    }
}
