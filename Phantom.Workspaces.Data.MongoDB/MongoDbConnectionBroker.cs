using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbConnectionBroker
{
    private const int DefaultMongoPort = 27017;

    private readonly ContainerEngine _containerEngine;

    internal ContainerEngine ContainerEngine => _containerEngine;
    private readonly MongoDbContainerDefinitionGenerator _containerDefinitionGenerator;
    private readonly IDockerDesktopLauncher _dockerDesktopLauncher;
    private readonly TimeSpan _dockerReadinessTimeout;
    private readonly TimeSpan _dockerReadinessPollInterval;
    private readonly int _connectVerificationAttempts;
    private readonly TimeSpan _connectRetryDelay;
    private readonly TimeProvider _timeProvider;

    public MongoDbConnectionBroker(
        ContainerEngine? containerEngine = null,
        MongoDbContainerDefinitionGenerator? containerDefinitionGenerator = null,
        int connectVerificationAttempts = 20,
        TimeSpan? connectRetryDelay = null,
        TimeProvider? timeProvider = null,
        IDockerDesktopLauncher? dockerDesktopLauncher = null,
        TimeSpan? dockerReadinessTimeout = null,
        TimeSpan? dockerReadinessPollInterval = null,
        ILogger<DockerCommandRunner>? dockerCommandRunnerLogger = null)
    {
        if (connectVerificationAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectVerificationAttempts));
        }

        // Build the default engine with the supplied logger so docker stdout/stderr is surfaced
        // instead of being discarded by a NullLogger (issue #1373). A caller-provided logger flows
        // straight through to the command runner.
        _containerEngine = containerEngine
            ?? new WindowsDockerDesktopEngine(dockerCommandRunnerLogger ?? NullLogger<DockerCommandRunner>.Instance);
        _containerDefinitionGenerator = containerDefinitionGenerator ?? new MongoDbContainerDefinitionGenerator();
        _connectVerificationAttempts = connectVerificationAttempts;
        // The operational client uses generous server-selection timeouts (see CreateClient), so each
        // readiness ping already waits for the server to become selectable on a cold start. A small
        // number of additional retries covers transient heartbeat drops while mongod stabilizes.
        _connectRetryDelay = connectRetryDelay ?? TimeSpan.FromSeconds(1);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dockerDesktopLauncher = dockerDesktopLauncher ?? new DockerDesktopLauncher();
        _dockerReadinessTimeout = dockerReadinessTimeout ?? TimeSpan.FromSeconds(120);
        _dockerReadinessPollInterval = dockerReadinessPollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async ValueTask<IMongoClient> GetClientAsync(
        MongoDbConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionDefinition);

        return connectionDefinition switch
        {
            MongoDbContainerConnectionDefinition containerConnection =>
                await ConnectContainerAsync(containerConnection, cancellationToken).ConfigureAwait(false),
            MongoDbExternalConnectionDefinition externalConnection =>
                await ConnectExternalAsync(externalConnection, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unsupported MongoDb connection provider: {connectionDefinition.Provider}.")
        };
    }

    /// <summary>
    /// Normalizes the data directory for a container connection: expands a leading <c>~</c> to the
    /// user's home directory and resolves the result to a full, absolute path (the container engine
    /// bind-mounts it and does not expand <c>~</c> or relative paths). The data directory must be
    /// configured by the caller (the wizard/GUI supplies the default); this layer applies <b>no</b>
    /// default and throws when none is set. Performs no I/O.
    /// </summary>
    public static MongoDbContainerConnectionDefinition NormalizeContainerDataDirectory(
        MongoDbContainerConnectionDefinition connectionDefinition)
    {
        ArgumentNullException.ThrowIfNull(connectionDefinition);

        if (string.IsNullOrWhiteSpace(connectionDefinition.DataDirectory))
        {
            throw new InvalidOperationException(
                "The MongoDB container data directory must be configured. Configure it through the "
                + "installation wizard or repository settings; the data layer applies no default.");
        }

        var normalizedDataDirectory = ExpandAndNormalizeDirectory(connectionDefinition.DataDirectory);

        return string.Equals(normalizedDataDirectory, connectionDefinition.DataDirectory, StringComparison.Ordinal)
            ? connectionDefinition
            : connectionDefinition.WithDataDirectory(normalizedDataDirectory);
    }

    /// <summary>
    /// Expands a leading <c>~</c> (home directory) and resolves the result to a full, absolute path.
    /// </summary>
    private static string ExpandAndNormalizeDirectory(string directory)
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (directory == "~")
        {
            return homeDirectory;
        }

        if (directory.StartsWith("~/", StringComparison.Ordinal)
            || directory.StartsWith("~\\", StringComparison.Ordinal))
        {
            directory = Path.Combine(homeDirectory, directory[2..]);
        }

        return Path.GetFullPath(directory);
    }

    private async ValueTask<IMongoClient> ConnectContainerAsync(
        MongoDbContainerConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken)
    {
        var resolvedConnectionDefinition = NormalizeContainerDataDirectory(connectionDefinition);

        // The data directory is bind-mounted into the container; it must exist before the container
        // is created, otherwise the container engine fails to start it.
        Directory.CreateDirectory(resolvedConnectionDefinition.DataDirectory);

        await EnsureContainerStartedAsync(resolvedConnectionDefinition, cancellationToken).ConfigureAwait(false);

        var connectionString = $"mongodb://localhost:{resolvedConnectionDefinition.HostPort ?? DefaultMongoPort}/?directConnection=true";
        var client = CreateClient(connectionString);
        await VerifyConnectionAsync(client, cancellationToken).ConfigureAwait(false);
        return client;
    }

    private async ValueTask<IMongoClient> ConnectExternalAsync(
        MongoDbExternalConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(connectionDefinition.ConnectionString);
        await VerifyConnectionAsync(client, cancellationToken).ConfigureAwait(false);
        return client;
    }

    private static IMongoClient CreateClient(
        string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        // The Atlas Local image (mongod + mongot) can briefly drop heartbeats during cold start and
        // while the search service warms up. Use generous operation timeouts so that real data
        // operations do not fail spuriously once the broker has handed back the client. Readiness is
        // bounded separately by VerifyConnectionAsync.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
        settings.ConnectTimeout = TimeSpan.FromSeconds(30);
        settings.SocketTimeout = TimeSpan.FromSeconds(120);
        return new MongoClient(settings);
    }

    private async ValueTask EnsureContainerStartedAsync(
        MongoDbContainerConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken)
    {
        await EnsureDockerEngineReadyAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _containerEngine.StartAsync(connectionDefinition.ContainerName, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var containerDefinition = _containerDefinitionGenerator.Generate(connectionDefinition);
            await _containerEngine.CreateAsync(containerDefinition, cancellationToken).ConfigureAwait(false);
            await _containerEngine.StartAsync(connectionDefinition.ContainerName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures the container engine is usable before we shell out to <c>docker</c> commands
    /// (issue #1299). If Docker Desktop is installed but not running, launches it and polls
    /// <see cref="ContainerEngine.UsableAsync"/> on <see cref="_dockerReadinessPollInterval"/>
    /// until it succeeds or <see cref="_dockerReadinessTimeout"/> elapses. Surfaces actionable
    /// errors when Docker Desktop is not installed or never becomes ready.
    /// </summary>
    private async ValueTask EnsureDockerEngineReadyAsync(CancellationToken cancellationToken)
    {
        if (await _containerEngine.UsableAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (_dockerDesktopLauncher.InstalledExecutablePath is null)
        {
            throw new InvalidOperationException(
                "Docker Desktop is not installed. Install Docker Desktop (%ProgramFiles%\\Docker\\Docker\\Docker Desktop.exe) "
                + "or configure an external MongoDB connection to use the local Mongo container.");
        }

        _dockerDesktopLauncher.LaunchDockerDesktop();

        var deadline = _timeProvider.GetUtcNow() + _dockerReadinessTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(_dockerReadinessPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);

            if (await _containerEngine.UsableAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new InvalidOperationException(
                    $"Docker Desktop was launched but the Docker engine did not become ready within "
                    + $"{_dockerReadinessTimeout.TotalSeconds:0} seconds. Ensure Docker Desktop finishes starting, "
                    + "then retry.");
            }
        }
    }

    private async ValueTask VerifyConnectionAsync(
        IMongoClient client,
        CancellationToken cancellationToken)
    {
        var adminDatabase = client.GetDatabase("admin");
        Exception? lastException = null;

        for (var attempt = 0; attempt < _connectVerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await adminDatabase
                    .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                lastException = ex;
                if (attempt == _connectVerificationAttempts - 1)
                {
                    break;
                }

                await Task.Delay(_connectRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "MongoDb connection could not be verified after retrying.",
            lastException);
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        return exception is MongoConnectionException
            or TimeoutException
            or MongoExecutionTimeoutException
            or MongoNotPrimaryException
            or MongoServerException;
    }
}
