using MongoDB.Bson;
using MongoDB.Driver;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbConnectionBroker
{
    private const int DefaultMongoPort = 27017;

    private readonly ContainerEngine _containerEngine;
    private readonly MongoDbContainerDefinitionGenerator _containerDefinitionGenerator;
    private readonly int _connectVerificationAttempts;
    private readonly TimeSpan _connectRetryDelay;

    public MongoDbConnectionBroker(
        ContainerEngine? containerEngine = null,
        MongoDbContainerDefinitionGenerator? containerDefinitionGenerator = null,
        int connectVerificationAttempts = 8,
        TimeSpan? connectRetryDelay = null)
    {
        if (connectVerificationAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectVerificationAttempts));
        }

        _containerEngine = containerEngine ?? new WindowsDockerDesktopEngine();
        _containerDefinitionGenerator = containerDefinitionGenerator ?? new MongoDbContainerDefinitionGenerator();
        _connectVerificationAttempts = connectVerificationAttempts;
        _connectRetryDelay = connectRetryDelay ?? TimeSpan.FromMilliseconds(250);
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

    private async ValueTask<IMongoClient> ConnectContainerAsync(
        MongoDbContainerConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken)
    {
        await EnsureContainerStartedAsync(connectionDefinition, cancellationToken).ConfigureAwait(false);

        var connectionString = $"mongodb://localhost:{connectionDefinition.HostPort ?? DefaultMongoPort}/?directConnection=true";
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
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
        settings.ConnectTimeout = TimeSpan.FromSeconds(1);
        settings.SocketTimeout = TimeSpan.FromSeconds(1);
        return new MongoClient(settings);
    }

    private async ValueTask EnsureContainerStartedAsync(
        MongoDbContainerConnectionDefinition connectionDefinition,
        CancellationToken cancellationToken)
    {
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

                await Task.Delay(_connectRetryDelay, cancellationToken).ConfigureAwait(false);
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
