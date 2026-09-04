using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Containers;

/// <summary>
/// Process-wide ambient logger factory for docker command execution (issue #1373). Application hosts
/// initialize <see cref="LoggerFactory"/> exactly once at startup with their real
/// <see cref="ILoggerFactory"/> (the same one built by <c>LoggingBootstrap</c> /
/// <c>HostFileLoggerFactory</c>), so that docker stdout/stderr is surfaced in the host log.
/// Components that shell out to docker without an explicitly supplied logger — notably the default
/// <c>MongoDbConnectionBroker</c> constructor used by the production persistence factories — obtain
/// their logger from <see cref="CreateLogger"/>.
/// <para>
/// It defaults to <see cref="NullLoggerFactory"/> so that processes which never initialize it —
/// unit tests in particular — stay quiet and behave exactly as before: <see cref="CreateLogger"/>
/// returns <see cref="NullLogger{T}.Instance"/> until a real factory is installed.
/// </para>
/// </summary>
public static class DockerCommandRunnerLogging
{
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// The ambient factory used to create docker command loggers. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/>; hosts assign their real factory at startup.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get => Volatile.Read(ref _loggerFactory);
        set => Volatile.Write(ref _loggerFactory, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Creates an <see cref="ILogger{DockerCommandRunner}"/> from the ambient factory. When the
    /// factory has not been initialized (still <see cref="NullLoggerFactory"/>), returns the shared
    /// <see cref="NullLogger{T}.Instance"/> so callers behave identically to the pre-#1373 default.
    /// </summary>
    public static ILogger<DockerCommandRunner> CreateLogger()
    {
        var factory = LoggerFactory;
        return ReferenceEquals(factory, NullLoggerFactory.Instance)
            ? NullLogger<DockerCommandRunner>.Instance
            : factory.CreateLogger<DockerCommandRunner>();
    }
}
