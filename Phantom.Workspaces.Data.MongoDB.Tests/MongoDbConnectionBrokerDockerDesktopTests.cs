using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // Exercises the DEFAULT / production wiring path (issue #1373): the persistence factories
    // construct the broker with NO arguments. Once an application host has initialized the ambient
    // docker logger factory, that no-arg default must yield a real, non-null logger — proving docker
    // stdout/stderr is no longer discarded via NullLogger in production.
    [Fact]
    public void MongoDbConnectionBroker_DefaultConstructor_AfterAmbientInit_UsesRealLoggerNotNullLogger()
    {
        var factory = new RecordingLoggerFactory();
        var previous = DockerCommandRunnerLogging.LoggerFactory;
        try
        {
            DockerCommandRunnerLogging.LoggerFactory = factory;

            // No-arg construction — identical to EntityRepository / AgentPersistenceStoreFactory /
            // FilesystemEditStoreFactory production call sites.
            var broker = new MongoDbConnectionBroker();

            var engine = Assert.IsType<WindowsDockerDesktopEngine>(broker.ContainerEngine);
            var runner = Assert.IsType<DockerCommandRunner>(engine.CommandRunner);
            Assert.NotNull(runner.Logger);
            Assert.NotSame(NullLogger<DockerCommandRunner>.Instance, runner.Logger);
            Assert.True(factory.CreateLoggerCallCount >= 1);
        }
        finally
        {
            DockerCommandRunnerLogging.LoggerFactory = previous;
        }
    }

    // Without an ambient factory (the unit-test default) the no-arg broker degrades to NullLogger so
    // tests stay quiet and behavior matches the pre-#1373 default.
    [Fact]
    public void MongoDbConnectionBroker_DefaultConstructor_WhenAmbientUninitialized_UsesNullLogger()
    {
        var previous = DockerCommandRunnerLogging.LoggerFactory;
        try
        {
            DockerCommandRunnerLogging.LoggerFactory = NullLoggerFactory.Instance;

            var broker = new MongoDbConnectionBroker();

            var engine = Assert.IsType<WindowsDockerDesktopEngine>(broker.ContainerEngine);
            var runner = Assert.IsType<DockerCommandRunner>(engine.CommandRunner);
            Assert.Same(NullLogger<DockerCommandRunner>.Instance, runner.Logger);
        }
        finally
        {
            DockerCommandRunnerLogging.LoggerFactory = previous;
        }
    }

    [Fact]
    public async Task MongoDbConnectionBroker_WhenContainerMissing_PullsImageBeforeCreate()
    {
        var engine = new FakeDockerEngine { UsableResult = true, FailFirstStart = true };
        var launcher = new FakeDockerDesktopLauncher("C:\\Docker Desktop.exe");
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time);

        _ = await InvokeGetClientAndCatchAsync(broker, SampleConnection());

        Assert.Equal(1, engine.PullCallCount);
        Assert.Equal(1, engine.CreateCallCount);
        Assert.Equal(new[] { "Start", "Pull", "Create", "Start" }, engine.CallSequence);
    }

    [Fact]
    public async Task MongoDbConnectionBroker_WhenPullFails_FallsBackToCreateUsingCachedImage()
    {
        var engine = new FakeDockerEngine { UsableResult = true, FailFirstStart = true, FailPull = true };
        var launcher = new FakeDockerDesktopLauncher("C:\\Docker Desktop.exe");
        var time = new FakeTimeProvider();
        var broker = CreateBroker(engine, launcher, time);

        _ = await InvokeGetClientAndCatchAsync(broker, SampleConnection());

        Assert.Equal(1, engine.PullCallCount);
        Assert.Equal(1, engine.CreateCallCount);
        Assert.True(engine.StartCallCount >= 2);
        Assert.Equal(new[] { "Start", "Pull", "Create", "Start" }, engine.CallSequence);
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

    // Stands in for a host's real ILoggerFactory: records that CreateLogger was consulted so the
    // default-broker test can prove the ambient factory (not NullLogger) produced the logger.
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public int CreateLoggerCallCount { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            this.CreateLoggerCallCount++;
            return new FakeDockerCommandRunnerLogger();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDockerEngine : ContainerEngine
    {
        public bool UsableResult { get; set; }

        public int UsableCallCount { get; private set; }

        public int StartCallCount { get; private set; }

        public int CreateCallCount { get; private set; }

        public int PullCallCount { get; private set; }

        // When true, the first StartAsync throws InvalidOperationException so the broker enters its
        // pull + create recovery path (mirrors a missing container).
        public bool FailFirstStart { get; set; }

        // When true, PullAsync throws InvalidOperationException (mirrors an offline/registry failure).
        public bool FailPull { get; set; }

        // Ordered record of lifecycle calls ("Pull", "Create", "Start") for assertion.
        public List<string> CallSequence { get; } = [];

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

        public override ValueTask PullAsync(string imageName, CancellationToken cancellationToken = default)
        {
            this.PullCallCount++;
            this.CallSequence.Add("Pull");
            if (this.FailPull)
            {
                throw new InvalidOperationException("Simulated docker pull failure.");
            }
            return ValueTask.CompletedTask;
        }

        public override ValueTask CreateAsync(ContainerDefinition definition, CancellationToken cancellationToken = default)
        {
            this.CreateCallCount++;
            this.CallSequence.Add("Create");
            return ValueTask.CompletedTask;
        }

        public override ValueTask StartAsync(string containerName, CancellationToken cancellationToken = default)
        {
            this.StartCallCount++;
            this.CallSequence.Add("Start");
            if (this.FailFirstStart && this.StartCallCount == 1)
            {
                throw new InvalidOperationException("Simulated missing container.");
            }
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
