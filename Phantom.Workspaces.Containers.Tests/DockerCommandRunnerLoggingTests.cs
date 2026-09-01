using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Containers.Tests;

/// <summary>
/// Tests for the process-wide ambient docker logger factory (issue #1373). Application hosts
/// initialize <see cref="DockerCommandRunnerLogging.LoggerFactory"/> at startup so the default
/// docker command runner logs stdout/stderr through the real host logger instead of discarding it.
/// </summary>
public sealed class DockerCommandRunnerLoggingTests
{
    [Fact]
    public void CreateLogger_WhenUninitialized_ReturnsNullLogger()
    {
        var previous = DockerCommandRunnerLogging.LoggerFactory;
        try
        {
            DockerCommandRunnerLogging.LoggerFactory = NullLoggerFactory.Instance;

            var logger = DockerCommandRunnerLogging.CreateLogger();

            Assert.Same(NullLogger<DockerCommandRunner>.Instance, logger);
        }
        finally
        {
            DockerCommandRunnerLogging.LoggerFactory = previous;
        }
    }

    [Fact]
    public void CreateLogger_AfterHostInitializesRealFactory_ReturnsRealLogger()
    {
        var factory = new RecordingLoggerFactory();
        var previous = DockerCommandRunnerLogging.LoggerFactory;
        try
        {
            // Mirrors what an application host does once at startup.
            DockerCommandRunnerLogging.LoggerFactory = factory;

            var logger = DockerCommandRunnerLogging.CreateLogger();

            Assert.NotNull(logger);
            Assert.NotSame(NullLogger<DockerCommandRunner>.Instance, logger);
            Assert.True(factory.CreateLoggerCallCount >= 1);
        }
        finally
        {
            DockerCommandRunnerLogging.LoggerFactory = previous;
        }
    }

    [Fact]
    public void LoggerFactory_SetNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DockerCommandRunnerLogging.LoggerFactory = null!);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public int CreateLoggerCallCount { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            this.CreateLoggerCallCount++;
            return NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }
}
