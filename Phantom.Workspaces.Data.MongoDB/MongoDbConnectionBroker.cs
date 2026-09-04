using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbConnectionBroker
{
    private const int DefaultMongoPort = 27017;

    // Atlas Local initialises its single-node replica set as "rs0"; used only when reconciling a
    // set that was never configured (existing data reuses its own persisted set name).
    private const string DefaultReplicaSetName = "rs0";

    private readonly ContainerEngine _containerEngine;

    internal ContainerEngine ContainerEngine => _containerEngine;
    private readonly MongoDbContainerDefinitionGenerator _containerDefinitionGenerator;
    private readonly IDockerDesktopLauncher _dockerDesktopLauncher;
    private readonly TimeSpan _dockerReadinessTimeout;
    private readonly TimeSpan _dockerReadinessPollInterval;
    private readonly int _connectVerificationAttempts;
    private readonly TimeSpan _connectRetryDelay;
    private readonly int _replicaSetSettleAttempts;
    private readonly int _replicaSetPrimaryAttempts;
    private readonly TimeSpan _replicaSetPollInterval;
    private readonly TimeSpan _serverSelectionTimeout;
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
        ILogger<DockerCommandRunner>? dockerCommandRunnerLogger = null,
        int replicaSetSettleAttempts = 5,
        int replicaSetPrimaryAttempts = 30,
        TimeSpan? replicaSetPollInterval = null,
        TimeSpan? serverSelectionTimeout = null)
    {
        if (connectVerificationAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectVerificationAttempts));
        }

        if (replicaSetSettleAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaSetSettleAttempts));
        }

        if (replicaSetPrimaryAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaSetPrimaryAttempts));
        }

        // Build the default engine so docker stdout/stderr is surfaced instead of being discarded by
        // a NullLogger (issue #1373). A caller-provided logger flows straight through to the command
        // runner. When no logger is supplied — the production factory path constructs the broker with
        // no arguments — fall back to the process-wide ambient docker logger, which application hosts
        // initialize at startup with their real ILoggerFactory. It degrades to NullLogger when
        // uninitialized (e.g. in unit tests), keeping tests quiet.
        _containerEngine = containerEngine
            ?? new WindowsDockerDesktopEngine(dockerCommandRunnerLogger ?? DockerCommandRunnerLogging.CreateLogger());
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
        _replicaSetSettleAttempts = replicaSetSettleAttempts;
        _replicaSetPrimaryAttempts = replicaSetPrimaryAttempts;
        _replicaSetPollInterval = replicaSetPollInterval ?? TimeSpan.FromSeconds(1);
        _serverSelectionTimeout = serverSelectionTimeout ?? TimeSpan.FromSeconds(30);
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

    private IMongoClient CreateClient(
        string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        // The Atlas Local image (mongod + mongot) can briefly drop heartbeats during cold start and
        // while the search service warms up. Use generous operation timeouts so that real data
        // operations do not fail spuriously once the broker has handed back the client. Readiness is
        // bounded separately by VerifyConnectionAsync. The server-selection timeout is injectable so
        // unit tests fail fast against an unreachable port instead of waiting the production default.
        settings.ServerSelectionTimeout = _serverSelectionTimeout;
        settings.ConnectTimeout = _serverSelectionTimeout;
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

            // Refresh the (moving :latest) image as its own observable step before creating the
            // container (issue #1374). Tolerant: if the pull fails (offline, registry down,
            // rate-limited) fall back to CreateAsync, which uses whatever image is cached locally.
            try
            {
                await _containerEngine.PullAsync(containerDefinition.ImageName, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Best-effort pull; proceed with the cached image.
            }

            await _containerEngine.CreateAsync(containerDefinition, cancellationToken).ConfigureAwait(false);
            await _containerEngine.StartAsync(connectionDefinition.ContainerName, cancellationToken).ConfigureAwait(false);
        }

        // #1415: the container is up, but a persisted /data/db replica-set config may still name a
        // stale (ephemeral container-id) host, leaving the node as RSGhost with no writable primary.
        // Reconcile the single-node set onto the now-stable host so writes (e.g. index creation)
        // succeed. Best-effort: readiness is still gated by VerifyConnectionAsync.
        await EnsureReplicaSetHasPrimaryAsync(connectionDefinition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the single-node replica set has a writable primary after the container starts
    /// (issue #1415). Machines whose persisted <c>/data/db</c> was pinned to a previous, ephemeral
    /// container hostname come up as <c>RSGhost</c> / <c>ReplicaSetNoPrimary</c>. When no primary is
    /// elected within a bounded wait, force-reconfigure the single member onto the stable host
    /// (<see cref="MongoDbContainerDefinitionGenerator.ReplicaSetHostname"/>) — or initiate the set
    /// when it was never configured — then wait for the primary. Uses a directConnection admin
    /// client so the command reaches the node regardless of its (broken) replica-set view.
    /// </summary>
    private async ValueTask EnsureReplicaSetHasPrimaryAsync(
        MongoDbContainerConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken)
    {
        var connectionString = $"mongodb://localhost:{connectionDefinition.HostPort ?? DefaultMongoPort}/?directConnection=true";
        var adminDatabase = CreateClient(connectionString).GetDatabase("admin");

        // Wait until the node accepts admin commands, giving a healthy set a brief chance to elect a
        // primary on its own. If it never becomes reachable there is nothing to reconcile here —
        // VerifyConnectionAsync remains the authority on readiness.
        BsonDocument? hello = await WaitUntilReachableAsync(adminDatabase, _replicaSetSettleAttempts, cancellationToken)
            .ConfigureAwait(false);
        if (hello is null)
        {
            return;
        }

        if (IsWritablePrimary(hello))
        {
            return;
        }

        // Reachable but no writable primary: the persisted config likely names a stale host. Force
        // the single-node set onto the stable host, then wait for the election to complete (#1415).
        try
        {
            await ForceReconfigureSingleNodeReplicaSetAsync(adminDatabase, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            return;
        }

        await WaitForWritablePrimaryAsync(adminDatabase, _replicaSetPrimaryAttempts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <c>hello</c> until the node responds (reachable), returning its response, or <c>null</c>
    /// if it never became reachable within <paramref name="attempts"/> tries. Connection failures are
    /// retried (the node may still be starting up); a successful writable-primary response returns
    /// immediately.
    /// </summary>
    private async ValueTask<BsonDocument?> WaitUntilReachableAsync(
        IMongoDatabase adminDatabase,
        int attempts,
        CancellationToken cancellationToken)
    {
        BsonDocument? lastResponse = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                lastResponse = await RunHelloAsync(adminDatabase, cancellationToken).ConfigureAwait(false);
                if (IsWritablePrimary(lastResponse))
                {
                    return lastResponse;
                }
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                // Node not accepting connections yet; keep waiting.
            }

            if (attempt == attempts - 1)
            {
                break;
            }

            await Task.Delay(_replicaSetPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return lastResponse;
    }

    /// <summary>
    /// Polls <c>hello</c> up to <paramref name="attempts"/> times and returns <c>true</c> once the
    /// node reports a writable primary. Connection failures are retried, since the node briefly drops
    /// connections while stepping up after a forced reconfig.
    /// </summary>
    private async ValueTask<bool> WaitForWritablePrimaryAsync(
        IMongoDatabase adminDatabase,
        int attempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (IsWritablePrimary(await RunHelloAsync(adminDatabase, cancellationToken).ConfigureAwait(false)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                // Transient during step-up; keep waiting.
            }

            if (attempt == attempts - 1)
            {
                break;
            }

            await Task.Delay(_replicaSetPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Forces the single-node replica set onto the stable host so an election can succeed even when
    /// the persisted config named a stale hostname; initiates the set when it was never configured.
    /// </summary>
    private static async ValueTask ForceReconfigureSingleNodeReplicaSetAsync(
        IMongoDatabase adminDatabase,
        CancellationToken cancellationToken)
    {
        var stableHost = $"{MongoDbContainerDefinitionGenerator.ReplicaSetHostname}:{DefaultMongoPort}";

        BsonDocument currentConfig;
        try
        {
            var configResponse = await adminDatabase
                .RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetConfig", 1), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            currentConfig = configResponse["config"].AsBsonDocument;
        }
        catch (MongoCommandException ex) when (IsReplicaSetUninitialized(ex))
        {
            // The replica set was never initialised (fresh /data/db): initiate a single-node set on
            // the stable host.
            var initialConfig = new BsonDocument
            {
                { "_id", DefaultReplicaSetName },
                {
                    "members",
                    new BsonArray { new BsonDocument { { "_id", 0 }, { "host", stableHost } } }
                },
            };

            await adminDatabase
                .RunCommandAsync<BsonDocument>(new BsonDocument { { "replSetInitiate", initialConfig } }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Repoint the single member at the stable host and bump the version, then force the reconfig
        // so the node accepts it without a current primary.
        currentConfig["members"] = new BsonArray
        {
            new BsonDocument { { "_id", 0 }, { "host", stableHost } },
        };
        currentConfig["version"] = currentConfig.TryGetValue("version", out var version) && version.IsInt32
            ? version.AsInt32 + 1
            : 1;

        await adminDatabase
            .RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    { "replSetReconfig", currentConfig },
                    { "force", true },
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsReplicaSetUninitialized(MongoCommandException exception)
    {
        // NotYetInitialized (94) / NoReplicationEnabled — the set has no config to read yet.
        return exception.Code == 94
            || string.Equals(exception.CodeName, "NotYetInitialized", StringComparison.Ordinal);
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

    internal async ValueTask VerifyConnectionAsync(
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
                // #1415: readiness requires a writable primary, not merely a reachable node. A
                // non-primary / RSGhost node answers `hello` (and `ping`) but rejects writes, so a
                // bare ping would hand back a client that fails later on index creation with
                // MongoNotPrimaryException. Retry until the node reports a writable primary.
                if (IsWritablePrimary(await RunHelloAsync(adminDatabase, cancellationToken).ConfigureAwait(false)))
                {
                    return;
                }

                lastException = new InvalidOperationException(
                    "MongoDb node is reachable but reports no writable primary (isWritablePrimary:false).");
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                lastException = ex;
            }

            if (attempt == _connectVerificationAttempts - 1)
            {
                break;
            }

            await Task.Delay(_connectRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "MongoDb connection could not be verified after retrying.",
            lastException);
    }

    /// <summary>
    /// Runs <c>hello</c> against the admin database and returns the raw response.
    /// </summary>
    private static async ValueTask<BsonDocument> RunHelloAsync(
        IMongoDatabase adminDatabase,
        CancellationToken cancellationToken)
    {
        return await adminDatabase
            .RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether a <c>hello</c> response indicates a writable primary
    /// (<c>isWritablePrimary == true</c>).
    /// </summary>
    private static bool IsWritablePrimary(BsonDocument? hello)
    {
        return hello is not null
            && hello.TryGetValue("isWritablePrimary", out var isWritablePrimary)
            && isWritablePrimary.IsBoolean
            && isWritablePrimary.AsBoolean;
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
